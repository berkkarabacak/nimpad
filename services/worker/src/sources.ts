import type { Job, JobSource } from './worker.ts';

/**
 * The job source used until Phase 6 adds the database-backed queue: no job is ever due, so `claim()` always
 * returns null and the worker idles. It does not pretend to process anything.
 */
export class EmptyJobSource implements JobSource {
  async claim(): Promise<Job | null> {
    return null;
  }

  async complete(job: Job): Promise<void> {
    throw new Error(`EmptyJobSource never hands out jobs, so it cannot complete job ${job.id}`);
  }

  async fail(job: Job): Promise<void> {
    throw new Error(`EmptyJobSource never hands out jobs, so it cannot fail job ${job.id}`);
  }
}
