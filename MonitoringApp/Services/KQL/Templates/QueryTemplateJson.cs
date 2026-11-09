using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

internal static class QueryTemplateJson
{
    internal const int MaxImportBytes = 65536;

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static void ValidateImport(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new QueryTemplateValidationException(QueryTemplateValidationCode.InputRequired, "Paste a query JSON document first.");
        if (Encoding.UTF8.GetByteCount(json) > MaxImportBytes)
            throw new QueryTemplateValidationException(QueryTemplateValidationCode.ImportTooLarge, "Query JSON must not exceed 64 KiB (UTF-8).");
    }
}
