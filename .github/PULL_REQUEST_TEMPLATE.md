<!--
Thanks. Two things this project cares about, and both have been violated before:

1. Measure, don't assume. Server-received fps is not presented fps — the panel decides.
   If a PR claims a performance number, say what was measured and under what conditions.
2. Don't claim features that don't exist. A README line about a gesture or a latency
   figure nobody verified is worse than no line.
-->

## What this changes

<!-- What does this do, and why? Link the issue it closes: Fixes #123 -->

## How it was verified

<!--
Required. Which of these did you actually run?
  dotnet build OpenWinSidecar.slnx
  dotnet test tests/OpenWinSidecar.Service.Tests/...
  node --test tests/viewer.runtime.test.cjs
If it touches the capture/encode path, say what you observed on screen, with numbers.
-->

- [ ] `dotnet build` clean
- [ ] .NET tests pass
- [ ] Viewer runtime tests pass (required for any change under `wwwroot/`)
- [ ] Verified on hardware, or explained why not

## Notes for the reviewer

<!--
Anything non-obvious: a trade-off you rejected, a protocol change that needs matching work
on the iOS client, a lock you had to take, a measurement you are unsure about.
-->
