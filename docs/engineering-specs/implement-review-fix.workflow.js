export const meta = {
  name: 'nimpad-implement-review-fix',
  description: 'Implement one NimPad area from a spec, review it with three independent lenses, then verify-and-fix',
  phases: [
    { title: 'Implement', detail: 'one engineer implements the spec' },
    { title: 'Review', detail: 'correctness, spec/design compliance, test adequacy' },
    { title: 'Fix', detail: 'verify each finding, fix real ones, re-run everything' },
  ],
}
// args: { key, area, context, task }
const { key, area, context, task } = args

const LENSES = [
  { key: 'correctness', prompt: 'CORRECTNESS & ROBUSTNESS: hunt for real bugs — logic errors, data loss, threading/dispatcher misuse (UI-thread affinity, async void, deadlocks, races), resource and event-handler leaks, unhandled exceptions, wrong edge cases, performance traps (O(n^2) on 10k items, work on the UI thread). For WPF: XAML resource keys that do not exist, bindings to members that do not exist (check names!), templates that would throw at runtime. Trace concrete failure scenarios.' },
  { key: 'spec', prompt: 'SPEC & DESIGN COMPLIANCE: compare against the task, docs/architecture.md, docs/implementation-plan.md, docs/design/README.md, docs/design/tokens.md and the deck page renders. Find every requirement that is missing, partially done, faked, or done differently from the design (sizes, states, copy text, keyboard behavior, accessibility/AutomationIds). Flag API design problems that will hurt the next layers.' },
  { key: 'tests', prompt: 'TEST ADEQUACY: judge whether the tests really prove the behavior. Find important behaviors with no test, vacuous tests, weak assertions, flaky timing, leaked files/threads. Run the test suites yourself and report the actual results. (WPF tests only run on Windows — check they are written so they will actually exercise the code there.)' },
]
const FINDINGS = {
  type: 'object',
  properties: {
    testRunSummary: { type: 'string' },
    findings: { type: 'array', items: { type: 'object', properties: {
      severity: { type: 'string', enum: ['critical', 'high', 'medium', 'low'] },
      file: { type: 'string' }, line: { type: 'number' }, title: { type: 'string' },
      detail: { type: 'string' }, suggestedFix: { type: 'string' },
    }, required: ['severity', 'file', 'title', 'detail', 'suggestedFix'] } },
  },
  required: ['testRunSummary', 'findings'],
}
const FIX_RESULT = {
  type: 'object',
  properties: {
    fixed: { type: 'array', items: { type: 'string' } },
    rejected: { type: 'array', items: { type: 'object', properties: { title: { type: 'string' }, reason: { type: 'string' } }, required: ['title', 'reason'] } },
    testResults: { type: 'string' },
    remainingRisks: { type: 'array', items: { type: 'string' } },
  },
  required: ['fixed', 'rejected', 'testResults', 'remainingRisks'],
}

phase('Implement')
const report = await agent(context + '\n\n' + task, { label: `impl:${key}`, phase: 'Implement' })

phase('Review')
const reviews = (await parallel(LENSES.map(l => () => agent(
  context + `\n\nYOU ARE A REVIEWER (read-only: do NOT modify any repository files; scratch experiments go under /tmp). Area under review: ${area}.\n` +
  `THE TASK THE IMPLEMENTER WAS GIVEN:\n${task}\n\nIMPLEMENTER REPORT:\n${report}\n\nYour lens — ${l.prompt}\n` +
  'Be specific (file + line). Report only issues you are confident are real, or requirements genuinely missing; no style nits. Rank by severity.',
  { label: `review:${key}:${l.key}`, phase: 'Review', schema: FINDINGS },
)))).filter(Boolean)
const findings = reviews.flatMap((rv, i) => rv.findings.map(f => ({ ...f, lens: LENSES[i] ? LENSES[i].key : 'n/a' })))
log(`${key}: ${findings.length} findings`)

phase('Fix')
const fix = await agent(
  context + `\n\nYOU OWN: ${area}. Reviewers produced the findings below. For EACH: verify it against the code (reproduce with a test where practical); if real, fix it properly and add or strengthen tests; if not real or not worth its cost, reject it with a concrete reason. Then run the full build, format check and every relevant test suite and make them green.\n\n` +
  `THE ORIGINAL TASK:\n${task}\n\nIMPLEMENTER REPORT:\n${report}\n\nREVIEWER TEST RUNS:\n${reviews.map(r => r.testRunSummary).join('\n')}\n\nFINDINGS (JSON):\n${JSON.stringify(findings, null, 1)}`,
  { label: `fix:${key}`, phase: 'Fix', schema: FIX_RESULT },
)
return { key, report, findings, fix }
