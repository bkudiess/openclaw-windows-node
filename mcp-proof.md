# Node mode UI MCP-only proof

Environment: isolated tray data dir (redacted path), branch bkudiess-node-mode-ui-enablement.
Settings: EnableMcpServer=true, EnableNodeMode=false.

## UI screenshot
See permissions-mcp-only-ui-cropped.png. It shows:
- Node mode toggle off
- Local MCP only status
- Serving 6 capabilities to local MCP clients at http://127.0.0.1:8765/
- Browser control toggle disabled in MCP-only mode
- System tools and Camera toggles still actionable

## Live MCP output
GET /:
OpenClaw MCP server. POST JSON-RPC to http://127.0.0.1:8765/

tools/list:
- total tools: 44
- selected tools: system.notify, system.which, canvas.present, screen.snapshot, camera.list, location.get, device.info

tools/call device.info:
{"systemName":"Windows","appVersion":"0.6.4-bkudiess-node-mode-ui-enablement.1","locale":"en-US"}

## MCP server logs
[2026-06-26 00:32:05.799] [INFO] Starting Windows Node in MCP-only mode (no gateway)
[2026-06-26 00:32:07.367] [INFO] Capabilities registered: system, canvas, screen, camera, location, device (6 caps)
[2026-06-26 00:32:07.388] [INFO] [MCP] HTTP server listening on http://<host>:8765/
[2026-06-26 00:32:07.390] [INFO] Started MCP-only node service without gateway connection
[2026-06-26 00:36:26.222] [DEBUG] [MCP] tools/call device.info
