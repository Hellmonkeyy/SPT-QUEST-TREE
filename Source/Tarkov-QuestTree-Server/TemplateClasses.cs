using System.Collections.Generic;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace QuestTreeServer
{
    /// <summary>The few item classes the weapon-build model has to tell apart, resolved the way
    /// the game does - by walking a template's parent chain - rather than by guessing from a
    /// property. Introduced in 1.14.0 for the magazine rule; the class ids are the game's own.</summary>
    public static class TemplateClasses
    {
        /// <summary>The Magazine class. Every magazine, drum and revolver cylinder descends from it;
        /// a grenade launcher's chamber and a weapon's own chamber do not.</summary>
        public static readonly MongoId Magazine = new("5448bc234bdc2d3c308b4569");

        /// <summary>Whether a template is (a descendant of) the given class. Bounded, because a
        /// modded template with a cyclic parent chain must not hang the server.</summary>
        public static bool IsA(IReadOnlyDictionary<MongoId, TemplateItem> items, TemplateItem? template, MongoId cls)
        {
            var current = template;

            for (var depth = 0; current != null && depth < 32; depth++)
            {
                if (current.Id == cls || current.Parent == cls) return true;
                if (!items.TryGetValue(current.Parent, out current)) return false;
            }

            return false;
        }
    }
}
