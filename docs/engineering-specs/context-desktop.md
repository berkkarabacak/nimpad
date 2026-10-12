You are a senior engineer implementing part of NimPad, a greenfield local-first Windows text editor (WPF +
AvalonEdit, .NET 10) with a cloud backend and an MCP/agent layer. Repository: /home/user/nimpad (git, branch
claude/vibrant-wright-a3yuns). We are delivering Phase 0 + Phase 1 of docs/product-spec.md.

READ FIRST: docs/architecture.md (contracts, layering, threading), docs/implementation-plan.md (approved plan),
docs/design/README.md (deck page index + documented adaptations, e.g. the tree-first sidebar),
docs/design/tokens.md. Design deck page renders (PNG, view them with your Read tool):
docs/design/renders/p-01.png ... p-29.png.

ALREADY DONE (committed, reviewed, tested — build on it, do not rewrite it):
- apps/windows/src/NimPad.Core (+ README.md): storage, NodeService, DocumentService, AutosaveScheduler,
  SearchService, SessionService/settings, ExternalFileService, LinkParser, change notifications, op queue.
- apps/windows/src/NimPad.App/Themes|Controls|Theming (+ Themes/README.md): the WPF design system — tokens,
  typography, metrics, icons, control styles, custom controls (Icon, SearchBox, SegmentedControl, Stepper, KeyCap,
  ShortcutKeys, Avatar, Banner, Spinner, SkeletonLine), ThemeService, SystemThemeWatcher, WindowThemeHelper,
  DesignGalleryWindow. Its tests in apps/windows/tests/NimPad.App.Tests/Themes pass on Windows CI.
- packages/contracts, services/*, database/* (TypeScript backend; not relevant to desktop work).

ENVIRONMENT
- Linux, 4 CPUs. .NET 10 SDK at ~/.dotnet: always
  `export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.
  Solution apps/windows/NimPad.slnx; central package versions in /home/user/nimpad/Directory.Packages.props
  (do not add packages). TreatWarningsAsErrors=true, Nullable enabled. CI also runs
  `dotnet format apps/windows/NimPad.slnx --verify-no-changes` — run `dotnet format` on what you touch.
- Tests: xunit.v3 on Microsoft.Testing.Platform. `dotnet test --project apps/windows/tests/NimPad.Core.Tests`,
  `... NimPad.Presentation.Tests` run here. The WPF project compiles on Linux (EnableWindowsTargeting) but cannot
  RUN here: NimPad.App.Tests builds but its tests need Windows — every WPF test must start with
  Assert.SkipUnless(OperatingSystem.IsWindows(), "...") and run on an STA thread (see
  apps/windows/tests/NimPad.App.Tests/Themes/Sta.cs). Windows CI (GitHub Actions windows-latest) runs them.
  XAML compile errors surface in `dotnet build`; runtime resource-lookup/template errors do NOT — be meticulous:
  only use resource keys documented in Themes/README.md; DynamicResource for theme-dependent values.
  A WPF Window must be Show()n (off-screen, ShowActivated=false, ShowInTaskbar=false) before layout works.

HARD RULES
- Do NOT run git commit/push/checkout/stash/reset or any git command that changes state (unless your task says you
  are in an isolated worktree). The orchestrator commits.
- Only modify files inside the paths you own (stated in your task). Never edit shared root config
  (Directory.*.props, global.json, package.json, pnpm files, .github/, docs/).
- No fake implementations: never simulate sync or AI, never skip tests to get green, never weaken assertions.
  Phase 1 shows "Local only" for sync state — never a fake "Synced".
- Public APIs get XML doc comments. Match the surrounding style.
- Finish with the whole solution building (0 warnings) and Core + Presentation tests passing. Your final message is
  a report for the orchestrator: files, public API summary, decisions, exact test results, what is unverified.
