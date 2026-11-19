# Classic HMI Graphics Catalog and Preview Design

## Intent

Extend `hmi_basic_studio` so an AI agent can discover graphics exposed by the selected HMI's TIA Portal V21 Openness provider and add a `GraphicView` reference to an approved screen import. The target workflow uses WinCC Basic and a KTP700 panel.

## Goals

- Enumerate graphics exposed through the selected HMI's public Openness `GraphicsProvider.Graphics` composition and report provider-observed coverage without inferring entry provenance.
- Support an optional reference to an existing project graphic in screen preview by cloning one `GraphicView` node from a caller-provided exported screen template, assigning it a fresh unique hex ID, setting its picture name and geometry, preserving the original prototype, and binding the exact preview artifacts to the existing approval digest.
- Do not claim to render Siemens graphic media or reproduce the source screen canvas faithfully in this phase. Preview identifies the TIA graphic reference by name and placement and warns that coordinates come from source XML.
- Import the approved screen through the existing Classic HMI pipeline and compile; do not import or replace graphic assets in this phase.
- Keep project reads on the verified binding and existing worker; never attach to or switch projects implicitly.

## Non-Goals

- Do not enumerate or extract graphics by scanning Siemens installation folders, reverse-engineering private storage, or automating Toolbox UI clicks.
- Do not distribute Siemens Toolbox files, thumbnails, or exported graphics in source, tests, packages, build output, or publish output.
- Do not claim global Toolbox coverage or project-local provenance unless the selected HMI's supported Openness API reports those facts.
- Do not mutate or save a project during catalog or preview operations.

## Constraints

- Siemens V21 XML documentation and reflection confirm `HmiTarget.GetService<GraphicsProvider>()` and `GraphicsProvider.Graphics`.
- The read-only live MCP probe on `TestTIA21` found the HMI-level provider unavailable. The standalone project provider returned 17 names, including `Graphic_1`, and a bound preview from the exported screen succeeded. Entry provenance and Toolbox coverage remain unverified.
- Classic HMI APIs remain behind `TIA_CLASSIC_HMI`; Siemens assemblies must not be shipped.
- Screen import remains explicitly approved and bound to the exact destination and preview digest. Compile failure may occur after screen import.
- The currently open TIA project was observed modified; do not import, compile, save, rebind, or close it during this work.

## Approaches

1. **Provider-observed public Openness catalog (selected):** list only objects returned by `GraphicsProvider.Graphics`; do not infer project-local or global Toolbox coverage.
2. **Private-folder scan or UI automation:** rejected as undocumented, unstable, and potentially unsafe for asset redistribution.

## Workflow

1. `hmi_basic_studio(action="list", device=...)` adds provider-returned graphics by name and coverage status.
2. `design_and_preview` accepts a picture name and placement (`x`, `y`, `width`, `height`), verifies the name exists in the selected HMI inventory, clones exactly one `Hmi.Screen.GraphicView` from the exported screen, assigns an unused hex ID, changes the clone's object name/placement/picture link, preserves the original node, and includes it in the approval digest.
3. Preview renders no graphic media. It shows a clearly labeled project-linked placeholder and the picture name/placement.
4. `import_and_compile` remains the current tag/screen/compile pipeline. It may import a screen referencing an existing graphic; graphic assets are not imported or replaced.

## Verification

- Unit tests cover inventory payloads, exact `GraphicView` reference/placement, missing/multiple prototype refusal, missing picture rejection, escaped HTML, and digest binding.
- Build with reference stubs and local TIA V21 assemblies; run all non-packaging tests.
- No live import or compile against the modified open project.

## Coverage Caveat

The first implementation exposes only graphics returned by `GraphicsProvider.Graphics`; it must label coverage as provider-observed, not project-local or global Toolbox coverage. Direct global Toolbox enumeration and binary image preview remain future work pending supported runtime verification.
