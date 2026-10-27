using System.IO;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

/// <summary>Covers the catalog side of right-click deleting a user preset: the entry leaves
/// the editable JSON, the in-memory lists refresh, built-ins stay protected, and the 最近
/// template history drops the id.</summary>
internal static class UserPresetDeleteTests
{
    public static void Run()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "disc-preset-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string presetPath = Path.Combine(directory, DiscPresetJsonStore.FileName);
            DiscPresetJsonStore.Save(presetPath, DiscPresetJsonStore.CreateInitialUserDocument());

            DiscParametersState state = new(presetPath);
            state.LoadPresets();
            Require(state.CdPresets.Any(p => p.Id == "user-custom-cd"), "seeded CD preset visible");

            string removed = state.RemoveUserPreset("user-custom-cd");
            Require(removed == "自定义 CD 参数", "removed preset reports its display name");
            Require(!state.CdPresets.Any(p => p.Id == "user-custom-cd"), "CD list refreshes without the entry");
            Require(state.DvdPresets.Any(p => p.Id == "user-custom-dvd"), "other families keep their presets");
            Require(state.BluRayPresets.Any(p => p.Id == "user-custom-bd"), "Blu-ray preset survives");

            DiscPresetJsonDocument reloaded = DiscPresetJsonStore.Load(presetPath);
            Require(reloaded.CdPresets.All(p => p.Id != "user-custom-cd"), "user JSON no longer lists the entry");
            Require(reloaded.DvdPresets.Any(p => p.Id == "user-custom-dvd"), "user JSON keeps other families");

            bool builtInRejected = false;
            try
            {
                state.RemoveUserPreset("cd-80");
            }
            catch (InvalidOperationException)
            {
                builtInRejected = true;
            }

            Require(builtInRejected, "built-in preset cannot be deleted");

            string savedName = state.AddUserPreset(
                OpticalDiscFamily.CompactDisc,
                "回归 CD 参数",
                24.5,
                56.8,
                359_849);
            Require(savedName == "回归 CD 参数", "saved preset reports the name it was given");
            CdDiscPreset saved = state.CdPresets.Single(preset => preset.DisplayName == savedName);
            Require(
                saved.Description.StartsWith("用户预设：", StringComparison.Ordinal),
                "preset description is derived from the geometry instead of a hand-written note");
            Require(
                DiscPresetJsonStore.Load(presetPath).CdPresets.Any(preset => preset.DisplayName == savedName),
                "saved preset reaches the user JSON");

            string recentPath = Path.Combine(directory, "recent-templates.json");
            RecentTemplateStore recent = new(recentPath);
            recent.Record("user-custom-cd");
            recent.Record("user-custom-dvd");
            recent.Remove("user-custom-cd");
            IReadOnlyList<string> remaining = recent.Load();
            Require(remaining.Count == 1 && remaining[0] == "user-custom-dvd", "recent history drops the deleted id");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("User preset delete check failed: " + name);
        }
    }
}
