# TIA Portal MCP Server Pro (`tia-portal-mcp-pro`)

[![.NET SDK](https://img.shields.io/badge/.NET-10.0-512BD4.svg?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![.NET Framework](https://img.shields.io/badge/.NET_Framework-4.8-512BD4.svg?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![TIA Portal](https://img.shields.io/badge/TIA_Portal-V21-009999.svg?style=flat-square&logo=siemens)](https://new.siemens.com/global/en/products/automation/industry-software/automation-software/tia-portal.html)
[![WinCC Support](https://img.shields.io/badge/WinCC-Unified_%26_Basic%2FComfort-009999.svg?style=flat-square)](https://github.com/FrozenBiu/tia-portal-mcp-pro)
[![MCP](https://img.shields.io/badge/MCP-Ready-000000.svg?style=flat-square)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/LICENSE)

Enhanced MCP server for Siemens SIMATIC TIA Portal V21 (forked and extended from [Czarnak/tia-portal-mcp](https://github.com/Czarnak/tia-portal-mcp)). It lets MCP clients and AI agents inspect, program, and design HMI screens in a running TIA Portal V21 project through the Siemens Openness API.

In addition to all upstream PLC, network, VCI, library, and WinCC Unified capabilities, **`tia-portal-mcp-pro`** adds **`hmi_basic_studio`** — a complete SimaticML XML design, interactive HTML preview, and Openness import/compile workflow for **WinCC Basic / Comfort / Classic** panels (such as **KTP700 Basic PN**).

## Tools

The server exposes **9 tools** in `read-only` (the startup default), **22 tools** in `read-write`, and the same **22 tools** in `full`.
`read-write` permits in-project edits, compilation and project lifecycle calls, asking once per actual lifecycle call.
`full` adds no tools or operations; it only runs lifecycle calls without the prompt. Every write tool
(`plc_write`, `network_write`, `hmi_write`, `hmi_basic_studio`, `vci_write`, `library_write` and the six lifecycle tools) uses one guarded single-call pipeline; there are no safety tokens and no PLC run/stop.

### WinCC Basic / Comfort HMI Studio (`hmi_basic_studio`)

- `hmi_basic_studio` - end-to-end engineering tool for classic WinCC Basic/Comfort HMI targets (`Siemens.Engineering.Hmi.dll`, e.g., KTP700 Basic `800x480`) available in `read-write` and `full` modes:
  - **`list` / `export`**: Discover `HmiTarget` devices, tag tables, screens, and provider-observed graphics (without inferring project or global Toolbox provenance), or export existing screens/tag tables to SimaticML `.xml`.
  - **`design_and_preview`**: Generate valid SimaticML tag tables (`Bool`, `Int`, `DInt`, `Real`, `String`) and screens from the bundled `templates/` library; optionally add a `GraphicView` reference to a provider-observed graphic name and placement. The HTML uses a labeled placeholder, not Siemens media, and warns that its canvas is illustrative. Preview and approve its digest **before** importing.
  - **`import_and_compile`**: Verify artifact SHA-256 digests, import the approved tag table and screen XML into TIA Portal via Openness (`ImportOptions.Override`), and trigger HMI compilation diagnostics.

### WinCC Unified HMI reads and writes

- `hmi_read` - run up to 50 read-only WinCC Unified HMI operations in one call (devices, tags, connections, alarms, logs, screens and items, faceplates, navigation, runtime settings, script modules, text and graphic lists, languages and `validate`). Registered in every access mode, paged with `offset`/`limit` (default 100), structured JSON with `contractVersion` `1.1`. Classic WinCC devices are listed here and managed via `hmi_basic_studio`. See the [HMI operations summary](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/SupportedOperations/HMI_OPERATIONS_SUMMARY.md).
- `hmi_write(operations, dryRun=false)` - create, update or delete WinCC Unified tag tables, tags and discrete/analog alarms: nine operations in read-write/full, up to 50 caller-ordered items on one verified standalone project or local session, with write contract `2.0`. Set `dryRun:true` for preview; execution stops at the first failure without batch rollback or server elicitation. `EngineeringTargetInvocationException` during mutation is `engineering_rejected` unless `portal_lost` takes precedence. Typed Siemens failures carry `engineeringError` except for `portal_lost`; partial outcomes are conditional on writer allocation. Inspect actual state, run public `validate` and compile the HMI device in TIA Portal after writes.

### PLC reads and cross-references

- `plc_read` - run up to 50 PLC read operations in one call: `get_block_content`, `get_type_content` and `list_tag_tables`. Each item carries a unique `operationId`, an `operation` name, and that operation's parameters. Items run independently, so a failing item does not stop the others. Results are structured JSON with `contractVersion` `1.0`; a value over the value budget (60,000 characters by default) is omitted whole with narrowing guidance, never cut.
- `read_cross_references` - read the cross-references of one project-tree target (a block, type, tag, constant, or a PLC, software unit or folder swept over every owner beneath it). Arguments: `target`, `filter`, `maxResults`. Both reads are available in every access mode and act on the bound project only; they never open or switch a project, and without a verified binding they fail closed with `binding_conflict` (call `bind_project` first).

### Version Control Interface

- `vci_read` - inspect standalone-project workspaces, paged mappings, optional raw status and supported leaf formats in every mode.
- `vci_write(operations, dryRun=false)` - create/delete workspaces, export leaf scopes, sync in either direction and delete mappings in read-write/full. It executes by default, previews with `dryRun:true`, and stops after the first failed item without rollback across items or server elicitation. Save the project to persist workspaces and mappings; inspect current state before retrying.

### Library reads & writes

- `library_read` - run up to 50 read-only library operations in one call: `list_global_libraries`, `inspect_library`, `list_children`, `list_versions` and `list_dependencies`. Registered in every access mode, structured JSON with `contractVersion` `1.0`. It needs a verified binding like every content tool (`bind_project` first) and never opens, retrieves, saves, closes or changes a library.
- `library_write(operations, dryRun=false)` - 22 operations in read-write/full, up to 50 caller-ordered items, write contract `2.0`: global-library lifecycle, folders, master copies and versioned FB/FC/PLC UDT types (`create_type`, `create_type_version`, `release_type_version`, `set_default_type_version`, `synchronize_type`, `instantiate_type_version`, `delete_type`, `delete_type_version`).

### PLC writes

- `plc_write(operations, dryRun=false)` - run up to 50 PLC software writes in caller order against one verified, already-open project. Operations: `update_block_logic`, `update_type_content`, `create_block` / `delete_block`, `create_block_group` / `delete_block_group`, `create_tag_table` / `delete_tag_table`, `create_tag` / `update_tag` / `delete_tag`, `create_user_constant` / `update_user_constant` / `delete_user_constant`. Set `dryRun:true` to preview effects and guards without mutation.

Content writes (`update_block_logic`, `update_type_content`) take the document as `content` together with `expectedContentHash`, the `contentHash` from a `plc_read` of the same object in the same format. Content changed since that read is rejected with `state_changed`.

Available `plc_read` operations: `get_block_content`, `get_type_content`, and `list_tag_tables`. `format=source` is available for global data blocks, PLC data types, and SCL-language FB/FC/OB; every other block language uses `format=xml`.

### Network operations

- `network_read` - run up to 50 dedicated network reads: `read_hardware_config`, `search_equipment_catalog`, `list_network_objects`, and `inspect_network_object`. Supports opt-in structured I/O extraction (`includeIoDetails`, `includeTagMatches`) and cursor pagination (`pageSize`, `cursor`).
- `network_write` - preview or apply up to 50 dedicated network writes: `add_network_device`, `configure_network_device`, `create_subnet`, `update_subnet`, and `delete_subnet`. Set `dryRun:true` to preview.

### Project tools

- `bind_project` — default `bind` adopts an already-open project in any mode; omit the path to adopt the sole open project or list candidates. Use `forceRebind:true` to switch.
- `get_project_status` — inspect active project metadata without opening, binding, or switching projects; takes no arguments.
- `browse_project_tree` — browse a canonical, paged v3 point-in-time project-tree snapshot with typed block metadata (`HeaderAuthor`, `HeaderVersion`, `HeaderFamily`, `HeaderName`), `startSelector`, `depth`, and `pageSize`.
- `compile_check` — compile a PLC, HMI, or selected block and return compiler messages; available in `read-write` and `full` modes.
- `open_project` / `create_project` / `save_project` / `save_project_as` / `archive_project` / `close_project` - guarded single-call lifecycle writes in `read-write` and `full`.

## Architecture

TIA Portal V21 ships its Openness API as .NET Framework 4.8 assemblies. Those assemblies use .NET Framework remoting APIs that cannot run correctly inside a .NET 10 process.

This project therefore uses two processes:

- `TiaMcpServer` - the .NET 10 MCP stdio server, SimaticML template/preview engine, and .NET global tool host.
- `TiaMcpServer.OpennessWorker` - a .NET Framework 4.8 worker process that dynamically resolves `Siemens.Engineering.dll` and `Siemens.Engineering.Hmi.dll` from the local TIA Portal V21 installation via Windows Registry (with graceful stub fallback if `Siemens.Engineering.WinCCUnified.dll` is not installed).

The MCP host keeps one persistent .NET Framework 4.8 worker process attached to TIA Portal and exchanges newline-delimited JSON over stdin/stdout. Bundled SimaticML templates live in `templates/` and are packaged alongside the server binary.

## Quick start (Install from `tia-portal-mcp-pro`)

> **Important:** Do **not** run `dotnet tool install -g TiaMcpServer` from public NuGet, as that installs the upstream 21-tool release without WinCC Basic / KTP700 support. Install directly from this repository instead.

### 1. Prerequisites

| Prerequisite                      | Notes                                                                 |
| --------------------------------- | --------------------------------------------------------------------- |
| Windows                           | Windows-only; the Siemens Openness API has no other host              |
| Siemens TIA Portal V21            | With TIA Portal Openness installed and enabled                        |
| `Siemens TIA Openness` group      | Current Windows user must be a member (sign out/restart after adding) |
| .NET 10 SDK (`10.0.400` or newer) | Required to build and host `TiaMcpServer`                             |
| .NET Framework 4.8 runtime        | Required by `TiaMcpServer.OpennessWorker`                             |

### 2. Clone and run the local installer

```powershell
git clone https://github.com/FrozenBiu/tia-portal-mcp-pro.git
cd tia-portal-mcp-pro
powershell -ExecutionPolicy Bypass -File ./scripts/install-local-tool.ps1
```

This script builds the solution in `Release` mode, bundles the `templates/` directory, installs/updates the local `tia-mcp` global tool, and verifies the installation.

### 3. Verify environment & register with your MCP client

Run `tia-mcp doctor` first to validate your Windows group membership, TIA Portal V21 registry paths, and Openness assemblies:

```powershell
tia-mcp doctor
```

Register with supported CLI clients directly, or add the JSON entry below to **Antigravity IDE** (`~/.gemini/config/mcp_config.json`), **Kilo Code**, or **Cursor**:

```powershell
# CLI registration (for Claude Code / Codex / OpenCode)
tia-mcp install claude-code --access-mode read-write
```

Manual MCP configuration (`mcp_config.json` / `mcp_settings.json`):

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "tia-mcp",
      "args": ["--access-mode", "read-write"]
    }
  }
}
```

_(Use `"--access-mode", "full"` if you want project lifecycle calls such as save/open/close to run without interactive elicitation prompts)._

## Documentation

**Using the server**

- [Installation](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/guides/installation.md) — install, verify with `doctor`, register with a client, access modes
- [MCP client configuration](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/guides/mcp-client-configuration.md) — client config reference, block path addressing and response size limits
- [Troubleshooting](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/guides/troubleshooting.md) — common failures and verified TIA Portal V21 behavior
- [Supported operations](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/SupportedOperations/README.md) — every operation by area, with parameters

**Understanding the design & development**

- [Architecture](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/ARCHITECTURE.md) — two-process topology, access enforcement, write safety
- [Building from source](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/development/building.md) — build, test, coverage, run locally
- [Packaging](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/docs/development/packaging.md) — build the package, install a local build as `tia-mcp`

## License

MIT — see [LICENSE](https://github.com/FrozenBiu/tia-portal-mcp-pro/blob/main/LICENSE).
