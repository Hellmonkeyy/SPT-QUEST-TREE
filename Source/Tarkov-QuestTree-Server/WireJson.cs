using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestTreeServer
{
    /// <summary>
    /// The one serializer setup every route answers with. The DTOs in QuestTreeDtos.cs carry no
    /// JsonPropertyName attributes: their wire names come from this camelCase policy, which used
    /// to be declared separately in each of the five files that serialize a payload. Dropping it
    /// from any one of them would have shipped PascalCase to a client that pins every name with
    /// JsonProperty and would have read the whole payload as defaults, silently.
    /// </summary>
    internal static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
    }
}
