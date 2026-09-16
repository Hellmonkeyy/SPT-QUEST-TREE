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
    ///
    /// FOR OUR OWN DTOs ONLY - primitives, strings, collections of those, and each other. An SPT
    /// domain type must NOT be passed through it, for two separate reasons that both fail quietly:
    ///
    /// Ids vanish. MongoId's only public property is IsEmpty, and the converter that turns it into a
    /// string is registered in SPT's options rather than attached to the type. With no converters
    /// here, an id serialises as {"isEmpty":false}.
    ///
    /// Names move. SPT types are not uniformly pinned - Item pins its own with JsonPropertyName, but
    /// the Upd it nests does not, so the camelCase policy above rewrites "Repairable" to
    /// "repairable", and the game reads those keys case-sensitively.
    ///
    /// Both shipped together once, in the preset echo on /questtree/build/save, where the first
    /// masked the second.
    ///
    /// ONE DTO still holds an SPT type: ProfileBuildDto.Tree, whose FittedPart carries a MongoId. It
    /// is safe only because [JsonIgnore] keeps it off the wire - /questtree/builds serialises that
    /// DTO through here. Removing that attribute to "send the tree as well" would emit
    /// "template":{"isEmpty":false} for every part, with nothing throwing on either side.
    ///
    /// Anything holding an SPT type goes through the injected JsonUtil instead, which is also what
    /// SaveServer writes the profile with, so the two agree by construction rather than by care.
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
