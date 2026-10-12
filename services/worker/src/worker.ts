// The job loop. Jobs come from a JobSource; Phase 6 adds the database-backed source (claiming rows with
// FOR UPDATE SKIP LOCKED, retries and the scheduler). This module only runs them: one job at a time, with
// per-job error isolation and a graceful stop that waits for the job in flight.

import type { WorkerLogger } from './logger.ts';

/** A unit of work claimed from a {@link JobSource}. */
export interface Job {
  /** Unique id assigned by the source. */
  readonly id: string;
  /** Selects the handler registered with {@link Worker.register}. */
  readonly name: string;
  /** Handler-specific input; handlers validate it themselves. */
  readonly payload: unknown;
  /** 1 for the first attempt, incremented by the source on each retry. */
  readonly attempt: number;
}

/** Where jobs come from and where their outcome is recorded. */
export interface JobSource {
  /**
   * Claims the next due job, or returns null when there is none. A claimed job must not be handed to another
   * worker until it is completed or failed (or its claim expires, as the source defines).
   *
   * @param signal Aborted when the worker is stopping; a source may use it to cut a long poll short.
   */
  claim(signal: AbortSignal): Promise<Job | null>;
  /** Records that `job` finished successfully. */
  complete(job: Job): Promise<void>;
  /** Records a failed attempt. Retry and backoff policy belong to the source. */
  fail(job: Job, error: Error): Promise<void>;
}

export interface JobContext {
  /** Aborted when the worker is asked to stop. Long-running handlers should honor it; the worker still waits. */
  readonly signal: AbortSignal;
  readonly log: WorkerLogger;
}

export type JobHandler = (job: Job, context: JobContext) => Promise<void>;

export interface WorkerOptions {
  source: JobSource;
  /** Wait after finding no job, or after the source failed, before polling again. Default 1000 ms. */
  pollIntervalMs?: number;
  logger?: WorkerLogger;
}

export const DEFAULT_POLL_INTERVAL_MS = 1_000;

const silentLogger: WorkerLogger = { error() {}, warn() {}, info() {}, debug() {} };

function toError(value: unknown): Error {
  return value instanceof Error ? value : new Error(`Non-error thrown: ${String(value)}`);
}

/** Resolves after `ms`, or immediately when `signal` aborts. Uses the global timer so fake timers apply. */
function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    const done = (): void => {
      clearTimeout(timer);
      signal.removeEventListener('abort', done);
      resolve();
    };
    const timer = setTimeout(done, ms);
    signal.addEventListener('abort', done, { once: true });
  });
}

/**
 * Polls a {@link JobSource} and runs each job with the handler registered for its name.
 *
 * - Jobs run one at a time. When a job was found, the next poll happens immediately; when none was found (or the
 *   source failed), the worker waits `pollIntervalMs`.
 * - A failing handler, an unknown job name, or a failing source call is logged and recorded; it never stops the
 *   loop.
 * - {@link stop} (or aborting the signal given to {@link start}) ends the loop after the job in flight finishes.
 */
export class Worker {
  readonly #source: JobSource;
  readonly #pollIntervalMs: number;
  readonly #log: WorkerLogger;
  readonly #handlers = new Map<string, JobHandler>();
  #controller: AbortController | null = null;
  #loop: Promise<void> | null = null;

  constructor(options: WorkerOptions) {
    const pollIntervalMs = options.pollIntervalMs ?? DEFAULT_POLL_INTERVAL_MS;
    if (!Number.isFinite(pollIntervalMs) || pollIntervalMs < 0) {
      throw new RangeError(`pollIntervalMs must be a non-negative number: ${pollIntervalMs}`);
    }
    this.#source = options.source;
    this.#pollIntervalMs = pollIntervalMs;
    this.#log = options.logger ?? silentLogger;
  }

  /** Registers the handler for jobs named `name`. Names are unique. */
  register(name: string, handler: JobHandler): this {
    if (name === '') {
      throw new TypeError('Job name must not be empty');
    }
    if (this.#handlers.has(name)) {
      throw new Error(`A handler for job "${name}" is already registered`);
    }
    this.#handlers.set(name, handler);
    return this;
  }

  /** Names of the registered handlers, in registration order. */
  get handlerNames(): string[] {
    return [...this.#handlers.keys()];
  }

  /** True between {@link start} and the moment the loop has fully stopped. */
  get isRunning(): boolean {
    return this.#loop !== null;
  }

  /**
   * Starts the polling loop. The returned promise resolves once the worker has stopped (via {@link stop} or
   * `signal`) and the job in flight, if any, has finished. It never rejects for job or source errors.
   *
   * @throws Error (as a rejected promise) when the worker is already running.
   */
  start(signal?: AbortSignal): Promise<void> {
    if (this.#loop !== null) {
      return Promise.reject(new Error('Worker is already running'));
    }
    const controller = new AbortController();
    const forwardAbort = (): void => controller.abort(signal?.reason);
    if (signal?.aborted) {
      controller.abort(signal.reason);
    } else {
      signal?.addEventListener('abort', forwardAbort, { once: true });
    }
    this.#controller = controller;
    this.#loop = this.#run(controller.signal).finally(() => {
      signal?.removeEventListener('abort', forwardAbort);
      this.#controller = null;
      this.#loop = null;
    });
    return this.#loop;
  }

  /** Asks the loop to stop and resolves after the job in flight (if any) has finished. Idempotent. */
  stop(): Promise<void> {
    if (this.#loop === null) {
      return Promise.resolve();
    }
    this.#controller?.abort();
    return this.#loop;
  }

  async #run(signal: AbortSignal): Promise<void> {
    this.#log.debug(
      { handlers: this.handlerNames, pollIntervalMs: this.#pollIntervalMs },
      'worker loop started',
    );
    while (!signal.aborted) {
      let job: Job | null;
      try {
        job = await this.#source.claim(signal);
      } catch (error) {
        if (!signal.aborted) {
          this.#log.error({ err: toError(error) }, 'claiming a job failed');
          await sleep(this.#pollIntervalMs, signal);
        }
        continue;
      }
      if (job === null) {
        await sleep(this.#pollIntervalMs, signal);
        continue;
      }
      // A job the source has handed over is ours even if a stop was requested meanwhile: run it rather than
      // leave it claimed but unprocessed.
      await this.#execute(job, signal);
    }
    this.#log.debug({}, 'worker loop stopped');
  }

  async #execute(job: Job, signal: AbortSignal): Promise<void> {
    const fields = { jobId: job.id, jobName: job.name, attempt: job.attempt };
    const handler = this.#handlers.get(job.name);
    if (handler === undefined) {
      const error = new Error(`No handler registered for job "${job.name}"`);
      this.#log.error({ ...fields, err: error }, 'job has no handler');
      await this.#record('fail', job, error);
      return;
    }
    const started = Date.now();
    this.#log.debug(fields, 'job started');
    try {
      await handler(job, { signal, log: this.#log });
    } catch (thrown) {
      const error = toError(thrown);
      this.#log.warn({ ...fields, err: error, durationMs: Date.now() - started }, 'job failed');
      await this.#record('fail', job, error);
      return;
    }
    this.#log.info({ ...fields, durationMs: Date.now() - started }, 'job completed');
    await this.#record('complete', job);
  }

  /** Reports an outcome to the source; a failure to do so is logged and does not stop the loop. */
  async #record(outcome: 'complete' | 'fail', job: Job, error?: Error): Promise<void> {
    try {
      if (outcome === 'complete') {
        await this.#source.complete(job);
      } else {
        await this.#source.fail(job, error ?? new Error('Job failed'));
      }
    } catch (recordError) {
      this.#log.error(
        { jobId: job.id, jobName: job.name, err: toError(recordError) },
        `recording job ${outcome === 'complete' ? 'completion' : 'failure'} failed`,
      );
    }
  }
}
