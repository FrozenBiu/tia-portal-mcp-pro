# Classic HMI Graphics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add provider-observed Classic HMI graphics discovery and approved GraphicView placement preview without distributing Siemens assets.

**Architecture:** Extend the existing `hmi_basic_studio` contract and worker `HmiBasicService` using the V21 public `GraphicsProvider.Graphics` composition. Update the XML/HTML preview builder to place a cloned GraphicView only when its picture name is in the selected HMI's provider-observed graphics, binding the final artifacts to the existing preview digest. Keep import on the current tag/screen/compile flow; do not import or replace graphic assets in this phase.

**Tech Stack:** C#/.NET 10 host, .NET Framework 4.8 Openness worker, Siemens TIA Portal V21 Classic WinCC API, xUnit, SimaticML XML.

**Spec:** `docs/superpowers/specs/2026-11-19-classic-hmi-graphics-design.md`

## Global Constraints

- Enumerate only objects returned by the selected HMI's public `GraphicsProvider.Graphics` composition; do not infer provenance.
- Never scan Siemens installation folders or bundle, commit, package, or cache Siemens graphic assets in the MCP distribution.
- Keep Classic API use conditional on `TIA_CLASSIC_HMI`; Siemens DLL references remain non-private and are not shipped.
- Do not import, compile, save, rebind, or otherwise mutate the currently open modified TIA project during verification.
- Require the existing verified binding and bind preview approval to the current project/HMI destination.
- Do not claim global Toolbox coverage or project-local provenance unless the public Openness API reports it.

## Review Focus

- No graphics service or null service: inventory must return empty graphics with explicit unavailable coverage rather than fail unrelated screen/tag listing.
- Duplicate graphic names in different scopes: reject ambiguous selectors if the API exposes paths.
- GraphicView prototype absent or malformed: reject requested placement without emitting invalid screen XML.
- Picture not present in selected HMI: reject bound preview instead of creating a dangling reference.
- Preview modified after approval: digest must cover the graphic reference and geometry embedded in screen XML/HTML.

---

### Task 1: Graphics inventory contract

**Files:**
- Modify: `TiaMcpServer.Contracts/HmiBasic/HmiBasicContracts.cs`
- Modify: `TiaMcpServer.Contracts/Safety/OperationPolicyCatalog.cs`
- Modify: `TiaMcpServer.OpennessWorker/Program.cs`
- Modify: `TiaMcpServer.OpennessWorker/Openness/HmiBasic/HmiBasicService.cs`
- Modify: `TiaMcpServer.Tests/Tools/HmiBasicStudioContractTests.cs`
- Modify: `TiaMcpServer.Tests/Tools/HmiBasicStudioProtocolTests.cs`

**Interfaces:**
- Extends existing `hmi_basic_list` results with `Graphics` and explicit provider-observed `GraphicsCoverage`; no extra worker round trip is added.
- Extends `HmiBasicListResult` validation to accept the new fields while rejecting malformed entries.
- Does not add graphic asset import or claim Toolbox/global catalog completeness.

- [ ] Write failing tests for graphics inventory payload validation, existing list Observe capability, and explicit unavailable coverage.
- [ ] Run focused tests and confirm they fail because the graphics contract does not exist.
- [ ] Implement the contract and list service. Query `GraphicsProvider.Graphics` on `HmiTarget` and fall back to the standalone project provider when unavailable. Return exact names and report `provider` or `unavailable` coverage.
- [ ] Run focused tests and confirm the inventory contract passes.

### Task 2: Approved GraphicView preview placement

**Files:**
- Modify: `TiaMcpServer/HmiBasic/HmiBasicStudioTools.cs`
- Modify: `TiaMcpServer/HmiBasic/HmiBasicDesigner.cs`
- Modify: `TiaMcpServer/HmiBasic/Assets/preview-template.html`
- Modify: `TiaMcpServer.Tests/Tools/HmiBasicStudioContractTests.cs`
- Modify: `TiaMcpServer.Tests/Tools/HmiBasicStudioProtocolTests.cs`

**Interfaces:**
- `spec.graphic` has `picture`, `objectName`, `x`, `y`, `width`, and `height`; coordinates are nonnegative and width/height positive.
- Bound `design_and_preview` validates `picture` against the selected HMI inventory before writing artifacts.
- Preview clones exactly one `Hmi.Screen.GraphicView`, assigns an unused hexadecimal ID, changes clone object name, bounds, and `LinkList/Picture/Name`, preserves the source node, writes a labeled TIA graphic-reference placeholder, not media, and warns that the 800x480 preview canvas is illustrative.
- Missing/multiple prototype nodes or unresolved picture names fail closed.
- Existing preview digest covers the final XML/HTML and destination identity.

- [ ] Write failing tests for placement cloning, preservation of the original node, fresh ID, link/name/geometry changes, missing/multiple prototype refusal, inventory mismatch, canvas bounds, escaped HTML, and digest changes after XML edit.
- [ ] Run focused tests and confirm they fail because placement is not implemented.
- [ ] Implement strict spec parsing and XML manipulation in `HmiBasicDesigner`; query selected device graphics before bound preview generation; show the non-rasterized placeholder.
- [ ] Run focused designer and protocol tests and confirm all preview assertions pass.

### Task 3: Documentation and verification

**Files:**
- Modify: `docs/SupportedOperations/HMI_OPERATIONS_SUMMARY.md`
- Modify: `README.md` only if the public tool summary needs updating.

- [ ] Update documentation to distinguish provider-observed graphics from project provenance and the global Toolbox catalog, and state that preview placement is not media rendering.
- [ ] Run `dotnet build TiaMcpServer.slnx -m:1 /p:UseTiaPortalReferenceStubs=true`.
- [ ] Run local V21-reference build with the installed PublicAPI path.
- [ ] Run `dotnet test TiaMcpServer.Tests --filter "Category!=Packaging"`.
- [ ] Inspect git status/diff and report that live import/compile was intentionally not run because the open project is modified.
