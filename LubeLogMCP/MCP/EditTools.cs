using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LubeLogMCP.Models;
using ModelContextProtocol.Server;

namespace LubeLogMCP.MCP;

public partial class LubeLoggerMCP
{
    [McpServerTool(Name = "update_service_record", ReadOnly = false, Destructive = false), Description("Edit an existing service record. Omitted fields/files are preserved. cost remains the invoice total. Requisitions APPEND consumption: do not retry a successful write. Attachments append after the update. dryRun validates without writes.")]
    public Task<string> UpdateServiceRecord(int recordId, DateTime? date = null, int? odometer = null, string? description = null, decimal? cost = null, string? notes = null, List<RequisitionInput>? requisitions = null, decimal? labourCost = null, decimal? partsCost = null, List<AttachmentInput>? attachments = null, bool dryRun = false)
        => UpdateWorkshop("service", recordId, date, odometer, description, cost, notes, requisitions, labourCost, partsCost, attachments, dryRun);
    [McpServerTool(Name = "update_repair_record", ReadOnly = false, Destructive = false), Description("Edit an existing repair record, preserving omitted fields/files. Requisitions append new consumption and must not be replayed after success. cost is the invoice total; labourCost/partsCost use configured extra fields. dryRun writes nothing.")]
    public Task<string> UpdateRepairRecord(int recordId, DateTime? date = null, int? odometer = null, string? description = null, decimal? cost = null, string? notes = null, List<RequisitionInput>? requisitions = null, decimal? labourCost = null, decimal? partsCost = null, List<AttachmentInput>? attachments = null, bool dryRun = false)
        => UpdateWorkshop("repair", recordId, date, odometer, description, cost, notes, requisitions, labourCost, partsCost, attachments, dryRun);
    [McpServerTool(Name = "update_upgrade_record", ReadOnly = false, Destructive = false), Description("Edit an existing upgrade record, preserving omitted fields/files. Requisitions append new consumption and must not be replayed after success. cost is the invoice total; labourCost/partsCost use configured extra fields. dryRun writes nothing.")]
    public Task<string> UpdateUpgradeRecord(int recordId, DateTime? date = null, int? odometer = null, string? description = null, decimal? cost = null, string? notes = null, List<RequisitionInput>? requisitions = null, decimal? labourCost = null, decimal? partsCost = null, List<AttachmentInput>? attachments = null, bool dryRun = false)
        => UpdateWorkshop("upgrade", recordId, date, odometer, description, cost, notes, requisitions, labourCost, partsCost, attachments, dryRun);

    private async Task<string> UpdateWorkshop(string type, int id, DateTime? date, int? odometer, string? description, decimal? cost, string? notes, List<RequisitionInput>? requisitions, decimal? labourCost, decimal? partsCost, List<AttachmentInput>? attachments, bool dryRun)
    {
        var data = await EditableRecord(type, id);
        if (date.HasValue) data["date"] = date.Value.ToString("yyyy-MM-dd");
        if (odometer.HasValue) data["odometer"] = odometer.Value;
        if (description != null) data["description"] = description;
        if (cost.HasValue) data["cost"] = cost.Value;
        if (notes != null) data["notes"] = notes;
        var extra = new PostRequestModel { Cost = decimal.Parse(data["cost"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture), Odometer = int.Parse(data["odometer"]!.ToString()) };
        await AddWorkshopFields(extra, type switch { "service" => ImportMode.ServiceRecord, "repair" => ImportMode.RepairRecord, _ => ImportMode.UpgradeRecord }, labourCost, partsCost, requisitions, attachments);
        var fields = data["extraFields"] as JsonArray ?? new JsonArray();
        foreach (var field in extra.ExtraFields)
        {
            for (var i = fields.Count - 1; i >= 0; i--) if (fields[i]?["name"]?.ToString() == field.Name) fields.RemoveAt(i);
            fields.Add(new JsonObject { ["name"] = field.Name, ["value"] = field.Value });
        }
        data["extraFields"] = fields;
        data["requisitions"] = JsonSerializer.SerializeToNode(extra.Requisitions);
        var response = await Send(new HttpRequestMessage(HttpMethod.Put, $"{instance}/api/vehicle/{type}records/update?dryRun={dryRun}") { Content = JsonContent.Create(data) });
        return await CompleteWorkshopAdd(response, type, attachments, dryRun, false);
    }

    [McpServerTool(Name = "update_supply_record", ReadOnly = false, Destructive = false), Description("Edit a vehicle or shop supply by id, preserving omitted fields/files/history. quantity and cost REPLACE the remaining stock balance/value; they do not purchase or requisition stock. Use workshop requisitions for consumption. dryRun validates and writes nothing.")]
    public async Task<string> UpdateSupplyRecord(int recordId, DateTime? date = null, string? description = null, decimal? quantity = null, decimal? cost = null, string? partNumber = null, string? partSupplier = null, string? notes = null, bool dryRun = false)
    {
        if (quantity < 0 || cost < 0) throw new ModelContextProtocol.McpException("Remaining quantity and cost cannot be negative");
        var data = await EditableRecord("supply", recordId);
        if (date.HasValue) data["date"] = date.Value.ToString("yyyy-MM-dd");
        if (description != null) data["description"] = description;
        if (quantity.HasValue) data["partQuantity"] = quantity.Value;
        if (cost.HasValue) data["cost"] = cost.Value;
        if (partNumber != null) data["partNumber"] = partNumber;
        if (partSupplier != null) data["partSupplier"] = partSupplier;
        if (notes != null) data["notes"] = notes;
        var result = JsonNode.Parse(await Send(new HttpRequestMessage(HttpMethod.Put, $"{instance}/api/vehicle/supplyrecords/update?dryRun={dryRun}") { Content = JsonContent.Create(data) }))!;
        result["additionalData"] = new JsonObject { ["recordId"] = recordId, ["dryRun"] = dryRun };
        return result.ToJsonString();
    }

    private async Task<JsonObject> EditableRecord(string type, int id)
    {
        var data = JsonNode.Parse(await GetRecordById(type, id))!.AsObject();
        // The invariant API may emit numeric strings as JSON numbers; input text fields require strings.
        foreach (var key in new[] { "description", "notes", "tags", "partNumber", "partSupplier" })
            if (data[key] != null) data[key] = data[key]!.ToString();
        if (data["extraFields"] is JsonArray fields)
            foreach (var field in fields) if (field?["value"] != null) field["value"] = field["value"]!.ToString();
        return data;
    }
}
