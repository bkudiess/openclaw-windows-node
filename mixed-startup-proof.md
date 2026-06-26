# Node mode UI mixed startup proof

Settings: EnableMcpServer=true, EnableNodeMode=false.
Profile: copied active local gateway profile with operator credential; secrets redacted.

## Connection page screenshot
See connection-mcp-only-active-gateway-cropped.png. It shows:
- Node mode toggle off
- Serving capabilities locally (MCP only)
- Local MCP endpoint reachable at http://127.0.0.1:8765/

## Startup and MCP logs
[2026-06-26 10:43:36.362] [INFO] Connecting to last successful gateway during startup: ws://<gateway> (identity.DeviceToken)
[2026-06-26 10:43:36.450] [INFO] gateway connected, waiting for challenge...
[2026-06-26 10:43:36.455] [INFO]   role=operator, clientId=cli, mode=cli
[2026-06-26 10:43:36.509] [INFO] [HANDSHAKE] Received hello-ok!
[2026-06-26 10:43:41.733] [INFO] [MCP] HTTP server listening on http://<host>:8765/
[2026-06-26 10:43:41.734] [INFO] Started MCP-only node service without gateway connection
[2026-06-26 10:43:42.140] [INFO] [App] Skipping local NodeService auto-connect because node mode is disabled
