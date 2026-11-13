# MCP tools

Transport: Streamable HTTP `/api/mcp`. Send `Authorization`, `x-api-key`, or `?apiKey=` with every request. No tool reads files from the agent's disk; document bytes travel as base64.

All names below are the MCP wire names. Read tools have `readOnlyHint:true`. Write tools have `dryRun:boolean=false`; true performs validation and permission checks but no stock, record or file writes. It does not reserve stock. No delete tools are exposed.

## Read tools

| Name | Arguments | Return |
|---|---|---|
| `get_lube_logger_mcp_status` | none | MCP/instance version status text |
| `get_vehicles` | none | Vehicle ids/descriptions |
| `get_vehicle_is_electric` | `vehicleId` | Electric-vehicle status text |
| `get_equipped_equipment` | `vehicleId` | JSON equipped-equipment list |
| `get_latest_odometer` | `vehicleId` | Latest odometer API response |
| `get_extra_fields` | `importMode` | Configured extra fields for the record type |
| `get_service_records` | `vehicleId` | JSON service records, including ids/files/extra fields/requisition history |
| `get_repair_records` | `vehicleId` | JSON repair records |
| `get_upgrade_records` | `vehicleId` | JSON upgrade records |
| `get_supply_records` | positive `vehicleId` | Vehicle stock, remaining quantity/value and supply ids |
| `get_shop_supply_records` | none | Enabled shop stock (`vehicleId=0`) |
| `get_record_by_id` | `recordType:service\|repair\|upgrade\|supply`, positive `recordId` | One accessible JSON record; error when missing/inaccessible |

## Write tools

Except where marked optional, listed arguments are required. Every row also accepts optional `dryRun=false`.

| Name | Arguments | Return |
|---|---|---|
| `add_fuel_record` | `vehicleId,date,odometer,volume,cost,fillToFull,missedFuelUp,startingSoc,endingSoc,extraFields` | API success with `additionalData.recordId` |
| `add_odometer_record` | `vehicleId,date,odometer,extraFields,equipmentRecordIds` | API success with `additionalData.recordId` |
| `add_vehicle_record` | `year,make,model,licensePlate,useEngineHours,odometerOptional,fuelType,extraFields` | API success with `additionalData.recordId` and original `additionalData.vehicleId` |
| `add_supply_record` | `vehicleId,date,description,quantity,cost,partNumber,partSupplier,extraFields` | API success with `additionalData.recordId` |
| `add_shop_supply_record` | Same supply args without `vehicleId` | API success with `additionalData.recordId` |
| `add_service_record` | `vehicleId,date,odometer,description,cost,extraFields`; optional `requisitions,labourCost,partsCost,attachments` | API success with `additionalData.recordId` |
| `add_repair_record` | Same as service | Same |
| `add_upgrade_record` | Same as service | Same |
| `update_service_record` | `recordId`; optional `date,odometer,description,cost,notes,requisitions,labourCost,partsCost,attachments` | API success with `additionalData.recordId`; preserves omitted data/files |
| `update_repair_record` | Same edit args | Same |
| `update_upgrade_record` | Same edit args | Same |
| `update_supply_record` | `recordId`; optional `date,description,quantity,cost,partNumber,partSupplier,notes` | API success with `additionalData.recordId`; applies to vehicle or shop stock |
| `attach_document_to_record` | `recordType:service\|repair\|upgrade,recordId,fileName,contentType,contentBase64` | API success with `additionalData.recordId,files` |

`extraFields` is an array of `{name,value}`; use `[]` when empty. Dates are ISO 8601. Enum values follow the tool's generated JSON schema. For fuel, `startingSoc=20,endingSoc=80` may be used for non-EVs.

`requisitions` is `[{supplyRecordId,quantity}]`, positive quantities. On both add and edit this APPENDS consumption, with atomic stock/record/history writes. Existing requisitions are not replaced. Do not replay a successful consumption request. Read the record first if a request's outcome is uncertain. Supply edits' `quantity`/`cost` replace the remaining stock balance/value; use a new supply record to record another purchase.

`cost` is always the invoice total, including whatever taxes/charges the invoice includes. `labourCost` maps to the configured extra field named exactly `Labour Cost`; `partsCost` maps to `Parts Cost`. Configure these fields on each applicable record type first. Neither amount recomputes or increases `cost`. Supply requisition reduces inventory value using the existing stock cost allocation; that need not equal the invoice's parts amount. GST may be passed through `extraFields` if configured.

`attachments` is `[{fileName,contentType,contentBase64}]`, PDF (`application/pdf`), JPG/JPEG (`image/jpeg`) or PNG (`image/png`). Extension, MIME and signature must match, with a combined decoded limit of 10 MiB. New storage names are random; original display names are sanitized. Existing files are preserved when attaching or editing.

## Invoice call order

1. Read the vehicle, configured extra fields and existing stock to avoid duplicate purchases/records.
2. Add any missing vehicle or shop supplies. Retain each returned `recordId`.
3. Dry-run a service/repair/upgrade add with the supply ids in `requisitions`, invoice total in `cost`, named labour/parts amounts and optional attachments.
4. Repeat with `dryRun=false`; retain the returned workshop `recordId`.
5. If attachments were not included, call `attach_document_to_record` with that id and the invoice bytes. Read back the record to confirm totals, history and files.

The stock/record transaction is atomic. Document upload follows the successful add/edit: if that upload fails, the response reports `success:false`, `recordSaved:true`, `recordId` and a retry instruction. Retry attachment on the saved id, not the requisition. Edit tools perform read/modify/update against the existing API; serialize edits to the same record because upstream has no ETag/concurrency token.
