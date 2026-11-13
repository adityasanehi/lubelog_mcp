using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LubeLogMCP.Models;
using ModelContextProtocol.Server;

namespace LubeLogMCP.MCP;

public partial class LubeLoggerMCP
{
    [McpServerTool(Name = "get_service_records", ReadOnly = true), Description("List service records for a vehicle, including ids, total cost, extra fields, files and requisition history.")]
    public Task<string> GetServiceRecords(int vehicleId) => ReadRecords("service", vehicleId);
    [McpServerTool(Name = "get_repair_records", ReadOnly = true), Description("List repair records for a vehicle.")]
    public Task<string> GetRepairRecords(int vehicleId) => ReadRecords("repair", vehicleId);
    [McpServerTool(Name = "get_upgrade_records", ReadOnly = true), Description("List upgrade records for a vehicle.")]
    public Task<string> GetUpgradeRecords(int vehicleId) => ReadRecords("upgrade", vehicleId);
    [McpServerTool(Name = "get_supply_records", ReadOnly = true), Description("List vehicle supplies with record ids and remaining quantity/cost. Use these ids in requisitions.")]
    public Task<string> GetSupplyRecords(int vehicleId) => vehicleId > 0 ? ReadRecords("supply", vehicleId) : throw new ArgumentException("vehicleId must be positive; use get_shop_supply_records for shop stock");
    [McpServerTool(Name = "get_shop_supply_records", ReadOnly = true), Description("List shared shop supplies. Shop supplies must be enabled in LubeLogger.")]
    public Task<string> GetShopSupplyRecords() => ReadRecords("supply", 0);
    [McpServerTool(Name = "get_record_by_id", ReadOnly = true), Description("Read one accessible service, repair, upgrade or supply record by id. Returns the record or an error; supply includes shop stock when enabled.")]
    public async Task<string> GetRecordById(string recordType, int recordId)
    {
        var type = RecordType(recordType);
        if (recordId <= 0) throw new ArgumentException("recordId must be positive");
        var text = await Send(new HttpRequestMessage(HttpMethod.Get, $"{instance}/api/vehicle/{type}records/all?id={recordId}"));
        var array = JsonNode.Parse(text) as JsonArray ?? throw new InvalidOperationException("Unexpected records response");
        if (array.Count != 1) throw new ArgumentException("Record not found or access denied");
        return array[0]!.ToJsonString();
    }

    private static string RecordType(string type) => type.ToLowerInvariant() switch
    {
        "service" => "service", "repair" => "repair", "upgrade" => "upgrade", "supply" => "supply",
        _ => throw new ArgumentException("recordType must be service, repair, upgrade or supply")
    };
    private Task<string> ReadRecords(string type, int vehicleId)
    {
        if (vehicleId < 0 || (vehicleId == 0 && type != "supply")) throw new ArgumentException("Invalid vehicleId");
        return Send(new HttpRequestMessage(HttpMethod.Get, $"{instance}/api/vehicle/{type}records?vehicleId={vehicleId}"));
    }
    private async Task<string> Send(HttpRequestMessage request)
    {
        using (request)
        {
            request.Headers.TryAddWithoutValidation("culture-invariant", "true");
            if (!request.Headers.Contains("Authorization") && !request.Headers.Contains("x-api-key")) AddAuthHeaders(request);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new ModelContextProtocol.McpException($"LubeLogger returned HTTP {(int)response.StatusCode}");
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj && obj["success"]?.GetValue<bool>() == false)
                throw new InvalidOperationException(body);
            if (node is JsonObject operation && operation["additionalData"] is JsonObject ids && ids["vehicleId"] != null && ids["recordId"] == null)
            {
                ids["recordId"] = ids["vehicleId"]!.DeepClone();
                return node.ToJsonString();
            }
            return body;
        }
    }

    [McpServerTool(Name = "attach_document_to_record", ReadOnly = false, Destructive = false), Description("Attach a PDF/JPG/PNG to an existing service/repair/upgrade record, preserving existing files. Call order: add supplies, add workshop record with requisitions, then attach using its returned recordId. Pass file bytes as base64; no filesystem paths. Maximum decoded file size 10 MiB. dryRun validates without saving a file or changing the record.")]
    public Task<string> AttachDocumentToRecord(string recordType, int recordId, string fileName, string contentType, string contentBase64, bool dryRun = false)
        => AttachDocuments(recordType, recordId, new List<AttachmentInput> { new() { FileName = fileName, ContentType = contentType, ContentBase64 = contentBase64 } }, dryRun);

    private static byte[] ValidateAttachment(AttachmentInput file)
    {
        const int max = 10 * 1024 * 1024;
        if (file == null || string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 255 || file.ContentBase64 == null || file.ContentBase64.Length > ((max + 2) / 3) * 4)
            throw new ArgumentException("Invalid document name or size (maximum 10 MiB decoded)");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(file.ContentBase64); }
        catch (FormatException) { throw new ArgumentException("contentBase64 must contain base64 file bytes"); }
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var valid = extension switch
        {
            ".pdf" => file.ContentType == "application/pdf" && bytes.AsSpan().StartsWith("%PDF-"u8),
            ".jpg" or ".jpeg" => file.ContentType == "image/jpeg" && bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            ".png" => file.ContentType == "image/png" && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            _ => false
        };
        if (!valid || bytes.Length == 0 || bytes.Length > max) throw new ArgumentException("Document size, MIME type, extension or signature invalid; use PDF/JPG/PNG up to 10 MiB");
        return bytes;
    }
    private async Task<string> AttachDocuments(string recordType, int recordId, List<AttachmentInput> files, bool dryRun)
    {
        var type = RecordType(recordType);
        if (type == "supply" || recordId <= 0) throw new ArgumentException("Attachment target must be an existing service, repair or upgrade record");
        var data = files.Select(f => (file: f, bytes: ValidateAttachment(f))).ToList();
        if (data.Count == 0 || data.Sum(x => (long)x.bytes.Length) > 10 * 1024 * 1024) throw new ArgumentException("Attachments must total at most 10 MiB");
        var content = new MultipartFormDataContent();
        foreach (var (file, bytes) in data)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            content.Add(part, "documents", Path.GetFileName(file.FileName.Replace('\\', '/')));
        }
        return await Send(new HttpRequestMessage(HttpMethod.Post, $"{instance}/api/workshop/{type}/{recordId}/documents?dryRun={dryRun}") { Content = content });
    }

    private async Task AddWorkshopFields(PostRequestModel data, ImportMode type, decimal? labourCost, decimal? partsCost, List<RequisitionInput>? requisitions, List<AttachmentInput>? attachments)
    {
        if (labourCost < 0 || partsCost < 0 || data.Cost < 0 || data.Odometer < 0) throw new ArgumentException("Costs and odometer must be nonnegative");
        data.Requisitions = requisitions ?? new();
        if (data.Requisitions.Any(x => x == null || x.SupplyRecordId <= 0 || x.Quantity <= 0)) throw new ArgumentException("Requisition ids and quantities must be positive");
        var files = attachments ?? new();
        if (files.Sum(x => (long)ValidateAttachment(x).Length) > 10 * 1024 * 1024) throw new ArgumentException("Attachments must total at most 10 MiB");
        if (labourCost == null && partsCost == null) return;
        var config = JsonNode.Parse(await Send(new HttpRequestMessage(HttpMethod.Get, $"{instance}/api/extrafields")))!.AsArray();
        foreach (var (name, amount) in new[] { ("Labour Cost", labourCost), ("Parts Cost", partsCost) })
        {
            if (amount == null) continue;
            if (config.Any(x => x?["recordType"]?.ToString() == type.ToString() && x["extraFields"]!.AsArray().Any(f => f?["name"]?.ToString() == name)) != true)
                throw new ArgumentException($"Configure the extra field '{name}' on {type} first");
            data.ExtraFields.RemoveAll(x => x.Name == name);
            data.ExtraFields.Add(new ExtraFieldPostModel { Name = name, Value = amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }
    private async Task<string> CompleteWorkshopAdd(string response, string type, List<AttachmentInput>? files, bool dryRun, bool created = true)
    {
        if (dryRun || files == null || files.Count == 0) return response;
        var node = JsonNode.Parse(response)!;
        var id = node["additionalData"]?["recordId"]?.GetValue<int>();
        if (node["success"]?.GetValue<bool>() != true || id == null) throw new InvalidOperationException("Add did not return a record id: " + response);
        try { await AttachDocuments(type, id.Value, files, false); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { success = false, recordCreated = created, recordSaved = true, recordId = id, message = "Record and requisitions saved; attachment failed. Retry attach_document_to_record on this id: " + ex.Message }); }
        return response;
    }
}
