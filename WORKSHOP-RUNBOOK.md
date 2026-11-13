# Workshop invoice API and MCP

API/MCP feature changes do not add UI controls. `cost` remains the invoice total; requisitions reduce remaining stock quantity and stock value but never add to the invoice total.

## Sync base

The app feature branch starts at merge `af2bed9`, incorporating 81 upstream commits from merge base `c0b2f29f9f1ff5467acd06c7adf1f2d3b6b11098` through `63fa5cd267497ba01c29f4888c65efbbdd4b94bf` (upstream 1.7.4, newer than the brief's 1.6.6).

Five conflicted files: vehicle deletion adopted upstream's shared helper, with Insurance/PUC deletion retained in that helper; user settings retained Insurance/PUC tabs and added upstream themes; dependency injection retained India storage/push services plus upstream health checks; Settings adopted upstream's paged layout and restored currency/push controls; settings JavaScript retained currency updates and added upstream theme/page/search functions. All other upstream UI changes are part of the sync accepted by the user.

The newly created MCP fork already matched upstream `1d72458a2ff622fb58e149d9bff96ece70a3df3f`; its sync range is empty and had no conflicts. Features are committed after these synced bases, with no direct pushes to main.

Upstream already provides record read/filter endpoints, record ids from standard add endpoints, multipart `/api/documents/upload`, and `files` on JSON add/update. Those are reused. Its MCP still had six read tools and eight add tools; requisition API inputs and the requested MCP reads/edits/attachments/dryRun were absent.

## API additions

- Service/repair/upgrade JSON add/update accept `requisitions: [{"supplyRecordId":123,"quantity":1}]`. API paths remain `/api/vehicle/{service|repair|upgrade}records/{add|update}`. Add uses `?vehicleId=...`; update uses `id` in the JSON body.
- Requisitions consume vehicle stock or enabled shop stock (`vehicleId=0`). Cross-vehicle stock, missing supplies, nonpositive quantities and aggregated quantities over available stock are rejected. Duplicate supply ids are aggregated. Stock/value, supply history, record history and automatic odometer insertion commit together or roll back. Existing stock costing/three-decimal rounding is reused.
- Updates append NEW requisitions. Omit/null to preserve existing history with the legacy API; `[]` appends nothing. Neither `[]` nor omission restores stock. Successful requests are not idempotent: read the returned record id before retrying after an uncertain result.
- Workshop reads now include `requisitionHistory`. Existing `/api/vehicle/{type}records?vehicleId=...&id=...` and `/all?id=...` supply the MCP read tools.
- `POST /api/workshop/{service|repair|upgrade}/{id}/documents` takes multipart `documents`, preserves existing files and appends all uploaded files in one record write. Aggregate limit: 10 MiB decoded. PDF/JPG/JPEG/PNG only, matching extension, MIME and signature. Uploaded names are sanitized and storage names randomized. Failed record saves clean up newly created files. A process crash between file creation and database commit can leave an unreferenced document file.
- `?dryRun=true` validates workshop writes/attachments and existing fuel, odometer, vehicle and supply add operations plus supply updates without saving records, stock or files. It is not a stock reservation. API key permission checks still apply. Workshop add/update responses retain `additionalData.recordId`.
- The shared JSON parameter filter now awaits execution and supports `application/json; charset=utf-8`, preventing the upstream filter's process-crash case uncovered by MCP tests.

## Test and rollout runbook

1. Keep the live app unchanged. Back up its whole `data` volume, not just the database, before deployment. Documents live under `data/documents`; the SSH-acquired DB-only test copy does not include historical document bytes.
2. Create extra fields named exactly `Labour Cost` and optionally `Parts Cost` for Service/Repair/Upgrade. MCP refuses an explicit amount when its named field is missing. Amounts are stored as invariant decimal strings; the invoice total is supplied separately.
3. Run `MCP_SOURCE=/path/to/lubelog_mcp bash Tests/run-workshop.sh` with .NET 10. The runner copies sources into a new temporary directory, seeds test-only credentials there, starts both services on loopback ports 18080/18081 and cleans up its processes/files. Optional `WORKSHOP_TEST_DB=/path/to/copy.db` starts from a DB copy; never supply an active DB file. Run in a disposable container with no published ports for copied private data.
4. GitHub Actions run the same checks in both repos, then build amd64/arm64 images. Branch pushes publish `sha-<full commit>` tags; main additionally updates `edge`; version tags are retained. The initial paired PRs use their counterpart's feature branch for integration. After both merge, set repository variables `WORKSHOP_MCP_REF=main` on the app and `WORKSHOP_APP_REF=main` on MCP before deleting feature branches.
5. Pin both images to their tested SHA tags/digests. For rollback, retain the pre-deployment app/MCP image digests and data-volume backup. Restore the prior image pair; no schema migration is required. Restore data only if needed, as that discards writes since the backup.
6. Keep the compose service name `app`, internal port 8080 and MCP `LUBELOG_INSTANCE=http://app:8080`. Send `Authorization`, `x-api-key`, or `?apiKey=` on every MCP request. Do not put account credentials into compose. MCP never needs the agent's disk.

## Verification boundary

Live SSH inspection found LiteDB in use and an empty Postgres application database. The user explicitly excluded Postgres testing. The new Postgres transaction path compiles but is runtime-unverified; there is no migration. Tests on the private LiteDB copy cover stock limits, duplicate ids, rollback, concurrency, cost totals, extra fields, attachments, dry runs, API key restrictions, all three auth passthrough forms and an MCP invoice flow. No production writes or live deployment are part of this PR.

Attachments run after successful workshop creation/update. If upload fails, MCP returns `success:false`, `recordSaved:true` and `recordId`; retry attachment on that id rather than adding/requisitioning again. Edit tools use existing read/update endpoints and preserve omitted data, but upstream has no ETags: serialize edits to the same record to avoid overwriting another writer's intervening edit. Standard pre-existing compiler/dependency warnings remain outside this feature.
