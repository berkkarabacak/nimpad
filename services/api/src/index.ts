import { contractsVersion } from '@nimpad/contracts';
import type { FastifyInstance } from 'fastify';
export const v: string = contractsVersion;
export type F = FastifyInstance;
