using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LLib.GameData;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;
using Questionable.Controller;
using Questionable.Data;
using Questionable.Model;
using Questionable.Model.Common;
using Questionable.Model.Questing;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Questionable.Windows.ConfigComponents;

internal sealed class SinglePlayerDutyConfigComponent : ConfigComponent
{
    private const string SinglePlayerDutyClipboardPrefix = "qst:single:";

    private static readonly List<(EClassJob ClassJob, string Name)> RoleQuestCategories =
    [
        (EClassJob.Paladin, "Tank Role Quests"),
        (EClassJob.WhiteMage, "Healer Role Quests"),
        (EClassJob.Lancer, "Melee Role Quests"),
        (EClassJob.Bard, "Physical Ranged Role Quests"),
        (EClassJob.BlackMage, "Magical Ranged Role Quests"),
    ];

#if false
    private readonly string[] _retryDifficulties = ["Normal", "Easy", "Very Easy"];
#endif

    private readonly TerritoryData _territoryData;
    private readonly QuestRegistry _questRegistry;
    private readonly QuestData _questData;
    private readonly IDataManager _dataManager;
    private readonly ClassJobUtils _classJobUtils;
    private readonly ILogger<SinglePlayerDutyConfigComponent> _logger;

    private ImmutableDictionary<EAetheryteLocation, List<SinglePlayerDutyInfo>> _startingCityBattles =
        ImmutableDictionary<EAetheryteLocation, List<SinglePlayerDutyInfo>>.Empty;

    private ImmutableDictionary<EExpansionVersion, List<SinglePlayerDutyInfo>> _mainScenarioBattles =
        ImmutableDictionary<EExpansionVersion, List<SinglePlayerDutyInfo>>.Empty;

    private ImmutableDictionary<EClassJob, List<SinglePlayerDutyInfo>> _jobQuestBattles =
        ImmutableDictionary<EClassJob, List<SinglePlayerDutyInfo>>.Empty;

    private ImmutableDictionary<EClassJob, List<SinglePlayerDutyInfo>> _roleQuestBattles =
        ImmutableDictionary<EClassJob, List<SinglePlayerDutyInfo>>.Empty;

    private ImmutableList<SinglePlayerDutyInfo> _otherRoleQuestBattles = ImmutableList<SinglePlayerDutyInfo>.Empty;

    private ImmutableList<(string Label, List<SinglePlayerDutyInfo>)> _otherQuestBattles =
        ImmutableList<(string Label, List<SinglePlayerDutyInfo>)>.Empty;

    public SinglePlayerDutyConfigComponent(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        TerritoryData territoryData,
        QuestRegistry questRegistry,
        QuestData questData,
        IDataManager dataManager,
        ClassJobUtils classJobUtils,
        ILogger<SinglePlayerDutyConfigComponent> logger)
        : base(pluginInterface, configuration)
    {
        _territoryData = territoryData;
        _questRegistry = questRegistry;
        _questData = questData;
        _dataManager = dataManager;
        _classJobUtils = classJobUtils;
        _logger = logger;
    }

    public void Reload()
    {
        List<ElementId> questsWithMultipleBattles = _territoryData.GetAllQuestsWithQuestBattles()
            .GroupBy(x => x.QuestId)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToList();

        List<SinglePlayerDutyInfo> mainScenarioBattles = [];
        Dictionary<EAetheryteLocation, List<SinglePlayerDutyInfo>> startingCityBattles =
            new()
            {
                { EAetheryteLocation.Limsa, [] },
                { EAetheryteLocation.Gridania, [] },
                { EAetheryteLocation.Uldah, [] },
            };

        List<SinglePlayerDutyInfo> otherBattles = [];

        Dictionary<ElementId, EClassJob> questIdsToJob = Enum.GetValues<EClassJob>()
            .Where(x => x != EClassJob.Adventurer && !x.IsCrafter() && !x.IsGatherer())
            .Where(x => x.IsClass() || !x.HasBaseClass())
            .SelectMany(x => _questRegistry.GetKnownClassJobQuests(x, false).Select(y => (y.QuestId, ClassJob: x)))
            .ToDictionary(x => x.QuestId, x => x.ClassJob);
        Dictionary<EClassJob, List<SinglePlayerDutyInfo>> jobQuestBattles = questIdsToJob.Values.Distinct()
            .ToDictionary(x => x, _ => new List<SinglePlayerDutyInfo>());

        Dictionary<ElementId, List<EClassJob>> questIdToRole = RoleQuestCategories
            .SelectMany(x => _questData.GetRoleQuests(x.ClassJob).Select(y => (y.QuestId, x.ClassJob)))
            .GroupBy(x => x.QuestId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ClassJob).ToList());
        Dictionary<EClassJob, List<SinglePlayerDutyInfo>> roleQuestBattles = RoleQuestCategories
            .ToDictionary(x => x.ClassJob, _ => new List<SinglePlayerDutyInfo>());
        List<SinglePlayerDutyInfo> otherRoleQuestBattles = [];

        foreach (var (questId, index, cfcData) in _territoryData.GetAllQuestsWithQuestBattles())
        {
            IQuestInfo questInfo = _questData.GetQuestInfo(questId);
            (bool enabled, SinglePlayerDutyOptions options) = FindDutyOptions(questId, index);

            string name = $"{FormatLevel(questInfo.Level)} {questInfo.Name}";
            if (!string.IsNullOrEmpty(cfcData.Name) && !questInfo.Name.EndsWith(cfcData.Name, StringComparison.Ordinal))
                name += $" ({cfcData.Name})";

            if (questsWithMultipleBattles.Contains(questId))
                name += $" (Part {options.Index + 1})";
            else if (cfcData.ContentFinderConditionId is 674 or 691)
                name += " (Melee/Phys. Ranged)";

            var dutyInfo = new SinglePlayerDutyInfo(name, questInfo, cfcData, options, enabled);

            if (dutyInfo.IsLimsaStart)
                startingCityBattles[EAetheryteLocation.Limsa].Add(dutyInfo);
            else if (dutyInfo.IsGridaniaStart)
                startingCityBattles[EAetheryteLocation.Gridania].Add(dutyInfo);
            else if (dutyInfo.IsUldahStart)
                startingCityBattles[EAetheryteLocation.Uldah].Add(dutyInfo);
            else if (questInfo.IsMainScenarioQuest)
                mainScenarioBattles.Add(dutyInfo);
            else if (questIdsToJob.TryGetValue(questId, out EClassJob classJob))
                jobQuestBattles[classJob].Add(dutyInfo);
            else if (questIdToRole.TryGetValue(questId, out var classJobs))
            {
                foreach (var roleClassJob in classJobs)
                    roleQuestBattles[roleClassJob].Add(dutyInfo);
            }
            else if (dutyInfo.IsOtherRoleQuest)
                otherRoleQuestBattles.Add(dutyInfo);
            else
                otherBattles.Add(dutyInfo);
        }

        _startingCityBattles = startingCityBattles
            .ToImmutableDictionary(x => x.Key,
                x => x.Value.OrderBy(y => y.SortKey)
                    .ToList());
        _mainScenarioBattles = mainScenarioBattles
            .GroupBy(x => x.Expansion)
            .ToImmutableDictionary(x => x.Key,
                x =>
                    x.OrderBy(y => y.JournalGenreId)
                        .ThenBy(y => y.SortKey)
                        .ThenBy(y => y.Index)
                        .ToList());
        _jobQuestBattles = jobQuestBattles
            .Where(x => x.Value.Count > 0)
            .ToImmutableDictionary(x => x.Key,
                x =>
                    x.Value
                        // level 10 quests use the same quest battle for [you started as this class] and [you picked this class up later]
                        .DistinctBy(y => y.ContentFinderConditionId)
                        .OrderBy(y => y.JournalGenreId)
                        .ThenBy(y => y.SortKey)
                        .ThenBy(y => y.Index)
                        .ToList());
        _roleQuestBattles = roleQuestBattles
            .ToImmutableDictionary(x => x.Key,
                x =>
                    x.Value.OrderBy(y => y.JournalGenreId)
                        .ThenBy(y => y.SortKey)
                        .ThenBy(y => y.Index)
                        .ToList());
        _otherRoleQuestBattles = otherRoleQuestBattles.ToImmutableList();
        _otherQuestBattles = otherBattles
            .OrderBy(x => x.JournalGenreId)
            .ThenBy(x => x.SortKey)
            .ThenBy(x => x.Index)
            .GroupBy(x => x.JournalGenreId)
            .Select(x => (BuildJournalGenreLabel(x.Key), x.ToList()))
            .ToImmutableList();
    }

    private (bool Enabled, SinglePlayerDutyOptions Options) FindDutyOptions(ElementId questId, byte index)
    {
        SinglePlayerDutyOptions options = new()
        {
            Index = 0,
            Enabled = false,
        };
        if (_questRegistry.TryGetQuest(questId, out var quest))
        {
            if (quest.Root.Disabled)
            {
                _logger.LogDebug("Disabling quest battle for quest {QuestId}, quest is disabled", questId);
                return (false, options);
            }
            else
            {
                var foundStep = quest.AllSteps()
                    .Select(x => x.Step)
                    .FirstOrDefault(x =>
                        x.InteractionType == EInteractionType.SinglePlayerDuty &&
                        x.SinglePlayerDutyIndex == index);
                if (foundStep == null)
                {
                    _logger.LogWarning(
                        "Disabling quest battle for quest {QuestId}, no battle with index {Index} found", questId,
                        index);
                    return (false, options);
                }
                else
                {
                    return (true, foundStep.SinglePlayerDutyOptions ?? options);
                }
            }
        }
        else
        {
            _logger.LogDebug("Disabling quest battle for quest {QuestId}, unknown quest", questId);
            return (false, options);
        }
    }

    private string BuildJournalGenreLabel(uint journalGenreId)
    {
        var journalGenre = _dataManager.GetExcelSheet<JournalGenre>().GetRow(journalGenreId);
        var journalCategory = journalGenre.JournalCategory.Value;

        string genreName = journalGenre.Name.ExtractText();
        string categoryName = journalCategory.Name.ExtractText();

        return $"{categoryName} \u203B {genreName}";
    }

    public override void DrawTab()
    {
        using var tab = ImRaii.TabItem("Quest Battles###QuestBattles");
        if (!tab)
            return;

        bool runSoloInstancesWithBossMod = Configuration.SinglePlayerDuties.RunSoloInstancesWithBossMod;
        if (ImGui.Checkbox("Run quest battles with BossMod", ref runSoloInstancesWithBossMod))
        {
            Configuration.SinglePlayerDuties.RunSoloInstancesWithBossMod = runSoloInstancesWithBossMod;
            Save();
        }

        using (ImRaii.PushIndent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X))
        {
            using (_ = ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
            {
                ImGui.TextUnformatted("Work in Progress:");
                ImGui.BulletText("Will always use BossMod for combat (ignoring the configured combat module).");
                ImGui.BulletText("Only a small subset of quest battles have been tested - most of which are in the MSQ.");
                ImGui.BulletText("When retrying a failed battle, it will always start at 'Normal' difficulty.");
                ImGui.BulletText("Please don't enable this option when using a BossMod fork (such as Reborn);\nwith the missing combat module configuration, it is unlikely to be compatible.");
            }

#if false
            using (ImRaii.Disabled(!runSoloInstancesWithBossMod))
            {
                ImGui.Spacing();
                int retryDifficulty = Configuration.SinglePlayerDuties.RetryDifficulty;
                if (ImGui.Combo("Difficulty when retrying a quest battle", ref retryDifficulty, _retryDifficulties,
                        _retryDifficulties.Length))
                {
                    Configuration.SinglePlayerDuties.RetryDifficulty = (byte)retryDifficulty;
                    Save();
                }
            }
#endif
        }

        ImGui.Separator();

        using (ImRaii.Disabled(!runSoloInstancesWithBossMod))
        {
            ImGui.Text(
                "Questionable includes a default list of quest battles that work if BossMod is installed.");
            ImGui.Text("The included list of quest battles can change with each update.");

            ImGui.Separator();
            ImGui.Text("You can override the settings for each individual quest battle:");


            using var tabBar = ImRaii.TabBar("QuestionableConfigTabs");
            if (tabBar)
            {
                DrawMainScenarioConfigTable();
                DrawJobQuestConfigTable();
                DrawRoleQuestConfigTable();
                DrawOtherQuestConfigTable();
            }

            DrawEnableAllButton();
            ImGui.SameLine();
            DrawClipboardButtons();
            ImGui.SameLine();
            DrawResetButton();
        }
    }

    private void DrawMainScenarioConfigTable()
    {
        var (totalEnabled, totalCount) = GetMainScenarioQuestCounts();
        using var tab = ImRaii.TabItem($"Main Scenario Quests ({totalEnabled}/{totalCount})###MSQ");
        if (!tab)
            return;

        using var child = BeginChildArea();
        if (!child)
            return;

        var (limsaEnabled, limsaTotal) = GetQuestBattleCounts(_startingCityBattles[EAetheryteLocation.Limsa]);
        string limsaHeaderText = $"Limsa Lominsa ({FormatLevel(5)} - {FormatLevel(14)}) ({limsaEnabled}/{limsaTotal})";
        string limsaKey = "Limsa";
        bool isLimsaHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(limsaKey, false);
        ImGui.SetNextItemOpen(isLimsaHeaderOpen, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(limsaHeaderText))
        {
            if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(limsaKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[limsaKey] = true;
                Save();
            }
            DrawQuestTable("LimsaLominsa", _startingCityBattles[EAetheryteLocation.Limsa]);
        }
        else
        {
            if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(limsaKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[limsaKey] = false;
                Save();
            }
        }

        var (gridaniaEnabled, gridaniaTotal) = GetQuestBattleCounts(_startingCityBattles[EAetheryteLocation.Gridania]);
        string gridaniaHeaderText = $"Gridania ({FormatLevel(5)} - {FormatLevel(14)}) ({gridaniaEnabled}/{gridaniaTotal})";
        string gridaniaKey = "Gridania";
        bool isGridaniaHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(gridaniaKey, false);
        ImGui.SetNextItemOpen(isGridaniaHeaderOpen, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(gridaniaHeaderText))
        {
            if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(gridaniaKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[gridaniaKey] = true;
                Save();
            }
            DrawQuestTable("Gridania", _startingCityBattles[EAetheryteLocation.Gridania]);
        }
        else
        {
            if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(gridaniaKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[gridaniaKey] = false;
                Save();
            }
        }

        var (uldahEnabled, uldahTotal) = GetQuestBattleCounts(_startingCityBattles[EAetheryteLocation.Uldah]);
        string uldahHeaderText = $"Ul'dah ({FormatLevel(4)} - {FormatLevel(14)}) ({uldahEnabled}/{uldahTotal})";
        string uldahKey = "Uldah";
        bool isUldahHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(uldahKey, false);
        ImGui.SetNextItemOpen(isUldahHeaderOpen, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(uldahHeaderText))
        {
            if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(uldahKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[uldahKey] = true;
                Save();
            }
            DrawQuestTable("Uldah", _startingCityBattles[EAetheryteLocation.Uldah]);
        }
        else
        {
            if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(uldahKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[uldahKey] = false;
                Save();
            }
        }

        foreach (EExpansionVersion expansion in Enum.GetValues<EExpansionVersion>())
        {
            if (_mainScenarioBattles.TryGetValue(expansion, out var dutyInfos))
            {
                var (enabledCount, totalCountForExpansion) = GetQuestBattleCounts(dutyInfos);
                string expansionHeaderText = $"{expansion.ToFriendlyString()} ({enabledCount}/{totalCountForExpansion})";
                string expansionKey = expansion.ToString();
                bool isExpansionHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(expansionKey, false);
                ImGui.SetNextItemOpen(isExpansionHeaderOpen, ImGuiCond.Always);
                if (ImGui.CollapsingHeader(expansionHeaderText))
                {
                    if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(expansionKey, false))
                    {
                        Configuration.SinglePlayerDuties.HeaderStates[expansionKey] = true;
                        Save();
                    }
                    DrawQuestTable($"Duties{expansion}", dutyInfos);
                }
                else
                {
                    if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(expansionKey, false))
                    {
                        Configuration.SinglePlayerDuties.HeaderStates[expansionKey] = false;
                        Save();
                    }
                }
            }
        }
    }

    private void DrawJobQuestConfigTable()
    {
        var (totalEnabled, totalCount) = GetJobQuestCounts();
        using var tab = ImRaii.TabItem($"Class/Job Quests ({totalEnabled}/{totalCount})###JobQuests");
        if (!tab)
            return;

        using var child = BeginChildArea();
        if (!child)
            return;

        int oldPriority = 0;
        foreach (var (classJob, priority) in _classJobUtils.SortedClassJobs)
        {
            if (classJob.IsCrafter() || classJob.IsGatherer())
                continue;

            if (_jobQuestBattles.TryGetValue(classJob, out var dutyInfos))
            {
                if (priority != oldPriority)
                {
                    oldPriority = priority;
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                }

                string jobName = classJob.ToFriendlyString();
                if (classJob.IsClass())
                    jobName += $" / {classJob.AsJob().ToFriendlyString()}";

                var (enabledCount, totalCountForJob) = GetQuestBattleCounts(dutyInfos);
                string jobHeaderText = $"{jobName} ({enabledCount}/{totalCountForJob})";
                string jobKey = classJob.ToString();
                bool isJobHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(jobKey, false);
                ImGui.SetNextItemOpen(isJobHeaderOpen, ImGuiCond.Always);
                if (ImGui.CollapsingHeader(jobHeaderText))
                {
                    if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(jobKey, false))
                    {
                        Configuration.SinglePlayerDuties.HeaderStates[jobKey] = true;
                        Save();
                    }
                    DrawQuestTable($"JobQuests{classJob}", dutyInfos);
                }
                else
                {
                    if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(jobKey, false))
                    {
                        Configuration.SinglePlayerDuties.HeaderStates[jobKey] = false;
                        Save();
                    }
                }
            }
        }
    }

    private void DrawRoleQuestConfigTable()
    {
        var (totalEnabled, totalCount) = GetRoleQuestCounts();
        using var tab = ImRaii.TabItem($"Role Quests ({totalEnabled}/{totalCount})###RoleQuests");
        if (!tab)
            return;

        using var child = BeginChildArea();
        if (!child)
            return;

        foreach (var (classJob, label) in RoleQuestCategories)
        {
            if (_roleQuestBattles.TryGetValue(classJob, out var dutyInfos))
            {
                var (enabledCount, totalCountForRole) = GetQuestBattleCounts(dutyInfos);
                string roleHeaderText = $"{label} ({enabledCount}/{totalCountForRole})";
                string roleKey = $"Role_{classJob}";
                bool isRoleHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(roleKey, false);
                ImGui.SetNextItemOpen(isRoleHeaderOpen, ImGuiCond.Always);
                if (ImGui.CollapsingHeader(roleHeaderText))
                {
                    if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(roleKey, false))
                    {
                        Configuration.SinglePlayerDuties.HeaderStates[roleKey] = true;
                        Save();
                    }
                    DrawQuestTable($"RoleQuests{classJob}", dutyInfos);
                }
                else
                {
                    if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(roleKey, false))
                    {
                        Configuration.SinglePlayerDuties.HeaderStates[roleKey] = false;
                        Save();
                    }
                }
            }
        }

        var (otherEnabled, otherTotal) = GetQuestBattleCounts(_otherRoleQuestBattles);
        string otherRoleHeaderText = $"General Role Quests ({otherEnabled}/{otherTotal})";
        string otherRoleKey = "Role_General";
        bool isOtherRoleHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(otherRoleKey, false);
        ImGui.SetNextItemOpen(isOtherRoleHeaderOpen, ImGuiCond.Always);
        if (ImGui.CollapsingHeader(otherRoleHeaderText))
        {
            if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(otherRoleKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[otherRoleKey] = true;
                Save();
            }
            DrawQuestTable("RoleQuestsGeneral", _otherRoleQuestBattles);
        }
        else
        {
            if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(otherRoleKey, false))
            {
                Configuration.SinglePlayerDuties.HeaderStates[otherRoleKey] = false;
                Save();
            }
        }
    }

    private void DrawOtherQuestConfigTable()
    {
        var (totalEnabled, totalCount) = GetOtherQuestCounts();
        using var tab = ImRaii.TabItem($"Other Quests ({totalEnabled}/{totalCount})###MiscQuests");
        if (!tab)
            return;

        using var child = BeginChildArea();
        if (!child)
            return;

        foreach (var (label, dutyInfos) in _otherQuestBattles)
        {
            var (enabledCount, totalCountForCategory) = GetQuestBattleCounts(dutyInfos);
            string otherHeaderText = $"{label} ({enabledCount}/{totalCountForCategory})";
            string otherKey = $"Other_{label}";
            bool isOtherHeaderOpen = Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(otherKey, false);
            ImGui.SetNextItemOpen(isOtherHeaderOpen, ImGuiCond.Always);
            if (ImGui.CollapsingHeader(otherHeaderText))
            {
                if (!Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(otherKey, false))
                {
                    Configuration.SinglePlayerDuties.HeaderStates[otherKey] = true;
                    Save();
                }
                DrawQuestTable($"Other{label}", dutyInfos);
            }
            else
            {
                if (Configuration.SinglePlayerDuties.HeaderStates.GetValueOrDefault(otherKey, false))
                {
                    Configuration.SinglePlayerDuties.HeaderStates[otherKey] = false;
                    Save();
                }
            }
        }
    }

    private void DrawQuestTable(string label, IReadOnlyList<SinglePlayerDutyInfo> dutyInfos)
    {
        using var table = ImRaii.Table(label, 2, ImGuiTableFlags.SizingFixedFit);
        if (table)
        {
            ImGui.TableSetupColumn("Quest", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Options", ImGuiTableColumnFlags.WidthFixed, 200f);

            foreach (var dutyInfo in dutyInfos)
            {
                ImGui.TableNextRow();

                string[] labels = dutyInfo.EnabledByDefault
                    ? SupportedCfcOptions
                    : UnsupportedCfcOptions;
                int value = 0;
                if (Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Contains(dutyInfo.ContentFinderConditionId))
                    value = 1;
                if (Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Contains(dutyInfo.ContentFinderConditionId))
                    value = 2;

                if (ImGui.TableNextColumn())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted(dutyInfo.Name);

                    if (ImGui.IsItemHovered() && Configuration.Advanced.AdditionalStatusInformation)
                    {
                        using var tooltip = ImRaii.Tooltip();
                        if (tooltip)
                        {
                            ImGui.TextUnformatted(dutyInfo.Name);
                            ImGui.Separator();
                            ImGui.BulletText($"TerritoryId: {dutyInfo.TerritoryId}");
                            ImGui.BulletText($"ContentFinderConditionId: {dutyInfo.ContentFinderConditionId}");
                        }
                    }

                    if (!dutyInfo.Enabled)
                    {
                        ImGuiComponents.HelpMarker("Questionable doesn't include support for this quest.",
                            FontAwesomeIcon.Times, ImGuiColors.DalamudRed);
                    }
                    else if (dutyInfo.Notes.Count > 0)
                        DrawNotes(dutyInfo.EnabledByDefault, dutyInfo.Notes);
                }

                if (ImGui.TableNextColumn())
                {
                    using var _ = ImRaii.PushId($"##Duty{dutyInfo.ContentFinderConditionId}");
                    using (ImRaii.Disabled(!dutyInfo.Enabled))
                    {
                        ImGui.SetNextItemWidth(200);
                        if (ImGui.Combo(string.Empty, ref value, labels, labels.Length))
                        {
                            Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Remove(dutyInfo.ContentFinderConditionId);
                            Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Remove(dutyInfo.ContentFinderConditionId);

                            if (value == 1)
                                Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Add(dutyInfo.ContentFinderConditionId);
                            else if (value == 2)
                                Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Add(dutyInfo.ContentFinderConditionId);

                            Save();
                        }
                    }
                }
            }
        }
    }

    private static ImRaii.IEndObject BeginChildArea() => ImRaii.Child("DutyConfiguration", new Vector2(675, 400), true);

    private void DrawEnableAllButton()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.CheckCircle, "Enable All"))
        {
            Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Clear();
            Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Clear();

            // Get all enabled quest battles and whitelist them
            var allEnabledDuties = GetAllEnabledSinglePlayerDuties();
            foreach (var duty in allEnabledDuties)
            {
                Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Add(duty.ContentFinderConditionId);
            }

            Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enable all of the quest battles, use at your own risk.");
    }

    private void DrawClipboardButtons()
    {
        using (ImRaii.Disabled(Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Count +
                   Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Count == 0))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Copy, "Export to clipboard"))
            {
                var whitelisted =
                    Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Select(x => $"{DutyWhitelistPrefix}{x}");
                var blacklisted =
                    Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Select(x => $"{DutyBlacklistPrefix}{x}");
                string text = SinglePlayerDutyClipboardPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    string.Join(DutyClipboardSeparator, whitelisted.Concat(blacklisted))));
                ImGui.SetClipboardText(text);
            }
        }

        ImGui.SameLine();

        string clipboardText = ImGui.GetClipboardText().Trim();
        using (ImRaii.Disabled(string.IsNullOrEmpty(clipboardText) ||
                               !clipboardText.StartsWith(SinglePlayerDutyClipboardPrefix, StringComparison.InvariantCulture)))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Paste, "Import from Clipboard"))
            {
                clipboardText = clipboardText.Substring(SinglePlayerDutyClipboardPrefix.Length);
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(clipboardText));

                Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Clear();
                Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Clear();
                foreach (string part in text.Split(DutyClipboardSeparator))
                {
                    if (part.StartsWith(DutyWhitelistPrefix, StringComparison.InvariantCulture) &&
                        uint.TryParse(part.AsSpan(DutyWhitelistPrefix.Length), CultureInfo.InvariantCulture,
                            out uint whitelistedCfcId))
                        Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Add(whitelistedCfcId);

                    if (part.StartsWith(DutyBlacklistPrefix, StringComparison.InvariantCulture) &&
                        uint.TryParse(part.AsSpan(DutyBlacklistPrefix.Length), CultureInfo.InvariantCulture,
                            out uint blacklistedCfcId))
                        Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Add(blacklistedCfcId);
                }

                Save();
            }
        }
    }

    private void DrawResetButton()
    {
        using (ImRaii.Disabled(!ImGui.IsKeyDown(ImGuiKey.ModCtrl)))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, "Reset to default"))
            {
                Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Clear();
                Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Clear();
                Save();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold CTRL to enable this button.");
    }

    private IEnumerable<SinglePlayerDutyInfo> GetAllEnabledSinglePlayerDuties()
    {
        return _startingCityBattles.Values.SelectMany(x => x)
            .Concat(_mainScenarioBattles.Values.SelectMany(x => x))
            .Concat(_jobQuestBattles.Values.SelectMany(x => x))
            .Concat(_roleQuestBattles.Values.SelectMany(x => x))
            .Concat(_otherRoleQuestBattles)
            .Concat(_otherQuestBattles.SelectMany(x => x.Item2))
            .Where(x => x.Enabled);
    }

    private (int enabledCount, int totalCount) GetQuestBattleCounts(IReadOnlyList<SinglePlayerDutyInfo> dutyInfos)
    {
        int enabledCount = 0;
        int totalCount = 0;

        foreach (var dutyInfo in dutyInfos)
        {
            if (dutyInfo.Enabled)
            {
                totalCount++;

                // a quest battle is considered "enabled" if:
                // it's whitelisted, OR
                // it's not blacklisted AND it's enabled by default
                bool isEnabled = Configuration.SinglePlayerDuties.WhitelistedSinglePlayerDutyCfcIds.Contains(dutyInfo.ContentFinderConditionId) ||
                               (!Configuration.SinglePlayerDuties.BlacklistedSinglePlayerDutyCfcIds.Contains(dutyInfo.ContentFinderConditionId) && dutyInfo.EnabledByDefault);

                if (isEnabled)
                    enabledCount++;
            }
        }

        return (enabledCount, totalCount);
    }

    private (int enabledCount, int totalCount) GetMainScenarioQuestCounts()
    {
        int totalEnabled = 0;
        int totalCount = 0;

        // Count starting city battles
        foreach (var battles in _startingCityBattles.Values)
        {
            var (enabled, total) = GetQuestBattleCounts(battles);
            totalEnabled += enabled;
            totalCount += total;
        }

        // Count main scenario battles by expansion
        foreach (var battles in _mainScenarioBattles.Values)
        {
            var (enabled, total) = GetQuestBattleCounts(battles);
            totalEnabled += enabled;
            totalCount += total;
        }

        return (totalEnabled, totalCount);
    }

    private (int enabledCount, int totalCount) GetJobQuestCounts()
    {
        int totalEnabled = 0;
        int totalCount = 0;

        foreach (var battles in _jobQuestBattles.Values)
        {
            var (enabled, total) = GetQuestBattleCounts(battles);
            totalEnabled += enabled;
            totalCount += total;
        }

        return (totalEnabled, totalCount);
    }

    private (int enabledCount, int totalCount) GetRoleQuestCounts()
    {
        int totalEnabled = 0;
        int totalCount = 0;

        foreach (var battles in _roleQuestBattles.Values)
        {
            var (enabled, total) = GetQuestBattleCounts(battles);
            totalEnabled += enabled;
            totalCount += total;
        }

        var (otherEnabled, otherTotal) = GetQuestBattleCounts(_otherRoleQuestBattles);
        totalEnabled += otherEnabled;
        totalCount += otherTotal;

        return (totalEnabled, totalCount);
    }

    private (int enabledCount, int totalCount) GetOtherQuestCounts()
    {
        int totalEnabled = 0;
        int totalCount = 0;

        foreach (var (_, battles) in _otherQuestBattles)
        {
            var (enabled, total) = GetQuestBattleCounts(battles);
            totalEnabled += enabled;
            totalCount += total;
        }

        return (totalEnabled, totalCount);
    }

    private sealed record SinglePlayerDutyInfo(
        string Name,
        IQuestInfo QuestInfo,
        TerritoryData.ContentFinderConditionData ContentFinderConditionData,
        SinglePlayerDutyOptions Options,
        bool Enabled)
    {
        public EExpansionVersion Expansion => QuestInfo.Expansion;
        public uint JournalGenreId => QuestInfo.JournalGenre ?? uint.MaxValue;
        public ushort SortKey => QuestInfo.SortKey;
        public uint ContentFinderConditionId => ContentFinderConditionData.ContentFinderConditionId;
        public uint TerritoryId => ContentFinderConditionData.TerritoryId;
        public byte Index => Options.Index;
        public bool EnabledByDefault => Options.Enabled;
        public ReadOnlyCollection<string> Notes => Options.Notes.AsReadOnly();

        public bool IsLimsaStart => ContentFinderConditionId is 332 or 333 or 313 or 334;
        public bool IsGridaniaStart => ContentFinderConditionId is 296 or 297 or 299 or 298;
        public bool IsUldahStart => ContentFinderConditionId is 335 or 312 or 337 or 336;

        /// <summary>
        /// 'Other' role quest is the post-EW/DT role quests.
        /// </summary>
        public bool IsOtherRoleQuest => ContentFinderConditionId is 845 or 1016;
    }
}