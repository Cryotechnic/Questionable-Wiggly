﻿using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ImGuiNET;
using LLib.ImGui;
using Questionable.Controller;
using Questionable.Functions;
using Questionable.Model;
using Questionable.Model.Questing;
using Questionable.Windows.QuestComponents;

namespace Questionable.Windows;

internal sealed class PriorityWindow : LWindow
{
    private const string ClipboardPrefix = "qst:v1:";
    private const char ClipboardSeparator = ';';

    private readonly QuestController _questController;
    private readonly QuestRegistry _questRegistry;
    private readonly QuestFunctions _questFunctions;
    private readonly QuestTooltipComponent _questTooltipComponent;
    private readonly UiUtils _uiUtils;
    private readonly IChatGui _chatGui;
    private readonly IDalamudPluginInterface _pluginInterface;

    private string _searchString = string.Empty;
    private ElementId? _draggedItem;

    public PriorityWindow(QuestController questController, QuestRegistry questRegistry, QuestFunctions questFunctions,
        QuestTooltipComponent questTooltipComponent, UiUtils uiUtils, IChatGui chatGui,
        IDalamudPluginInterface pluginInterface)
        : base("Quest Priority###QuestionableQuestPriority")
    {
        _questController = questController;
        _questRegistry = questRegistry;
        _questFunctions = questFunctions;
        _questTooltipComponent = questTooltipComponent;
        _uiUtils = uiUtils;
        _chatGui = chatGui;
        _pluginInterface = pluginInterface;

        Size = new Vector2(400, 400);
        SizeCondition = ImGuiCond.Once;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 400),
            MaximumSize = new Vector2(400, 999)
        };
    }

    public override void Draw()
    {
        ImGui.Text("Quests to do first:");
        DrawQuestFilter();
        DrawQuestList();

        List<ElementId> clipboardItems = ParseClipboardItems();
        ImGui.BeginDisabled(clipboardItems.Count == 0);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, "Import from Clipboard"))
            ImportFromClipboard(clipboardItems);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(_questController.ManualPriorityQuests.Count == 0);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Upload, "Export to Clibpoard"))
            ExportToClipboard();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Remove finished Quests"))
            _questController.ManualPriorityQuests.RemoveAll(q => _questFunctions.IsQuestComplete(q.Id));
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, "Clear"))
            _questController.ManualPriorityQuests.Clear();
        ImGui.EndDisabled();

        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(
            "If you have an active MSQ quest, Questionable will generally try to do:");
        ImGui.BulletText("'Priority' quests: class quests, ARR primals, ARR raids");
        ImGui.BulletText(
            "Supported quests in your 'To-Do list'\n(quests from your Journal that are always on-screen)");
        ImGui.BulletText("MSQ quest (if available, unless it is marked as 'ignored'\nin your Journal)");
        ImGui.TextWrapped(
            "If you don't have any active MSQ quest, it will always try to pick up the next quest in the MSQ first.");
    }

    private void DrawQuestFilter()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo($"##QuestSelection", "Add Quest...", ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            bool addFirst = ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);

            IEnumerable<Quest> foundQuests;
            if (!string.IsNullOrEmpty(_searchString))
            {
                foundQuests = _questRegistry.AllQuests
                    .Where(x => x.Id is not SatisfactionSupplyNpcId and not AlliedSocietyDailyId)
                    .Where(x => x.Info.Name.Contains(_searchString, StringComparison.CurrentCultureIgnoreCase))
                    .Where(x => !_questFunctions.IsQuestUnobtainable(x.Id));
            }
            else
            {
                foundQuests = _questRegistry.AllQuests.Where(x => _questFunctions.IsQuestAccepted(x.Id));
            }

            foreach (var quest in foundQuests)
            {
                if (quest.Info.IsMainScenarioQuest || _questController.ManualPriorityQuests.Any(x => x.Id == quest.Id))
                    continue;

                bool addThis = ImGui.Selectable(quest.Info.Name);
                if (addThis || addFirst)
                {
                    _questController.ManualPriorityQuests.Add(quest);

                    if (addFirst)
                    {
                        ImGui.CloseCurrentPopup();
                        addFirst = false;
                    }
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
    }

    private void DrawQuestList()
    {
        List<Quest> priorityQuests = _questController.ManualPriorityQuests;
        Quest? itemToRemove = null;
        Quest? itemToAdd = null;
        int indexToAdd = 0;

        float width = ImGui.GetContentRegionAvail().X;
        List<(Vector2 TopLeft, Vector2 BottomRight)> itemPositions = [];

        for (int i = 0; i < priorityQuests.Count; ++i)
        {
            Vector2 topLeft = ImGui.GetCursorScreenPos() +
                              new Vector2(0, -ImGui.GetStyle().ItemSpacing.Y / 2);
            var quest = priorityQuests[i];
            ImGui.PushID($"Quest{quest.Id}");

            var style = _uiUtils.GetQuestStyle(quest.Id);
            bool hovered;
            using (var _ = _pluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(style.Color, style.Icon.ToIconString());
                hovered = ImGui.IsItemHovered();
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(quest.Info.Name);
            hovered |= ImGui.IsItemHovered();

            if (hovered)
                _questTooltipComponent.Draw(quest.Info);

            if (priorityQuests.Count > 1)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                               ImGui.GetStyle().WindowPadding.X -
                               ImGui.CalcTextSize(FontAwesomeIcon.ArrowsUpDown.ToIconString()).X -
                               ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                               ImGui.GetStyle().FramePadding.X * 4 -
                               ImGui.GetStyle().ItemSpacing.X);
                ImGui.PopFont();

                if (_draggedItem == quest.Id)
                {
                    ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown,
                        ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ButtonActive)));
                }
                else
                    ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown);

                if (_draggedItem == null && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    _draggedItem = quest.Id;

                ImGui.SameLine();
            }
            else
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                               ImGui.GetStyle().WindowPadding.X -
                               ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                               ImGui.GetStyle().FramePadding.X * 2);
                ImGui.PopFont();
            }

            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = quest;

            ImGui.PopID();

            Vector2 bottomRight = new Vector2(topLeft.X + width,
                ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + 2);
            itemPositions.Add((topLeft, bottomRight));
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _draggedItem = null;
        else if (_draggedItem != null)
        {
            var draggedItem = priorityQuests.Single(x => x.Id == _draggedItem);
            int oldIndex = priorityQuests.IndexOf(draggedItem);

            var (topLeft, bottomRight) = itemPositions[oldIndex];
            ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(ImGuiColors.DalamudGrey), 3f,
                ImDrawFlags.RoundCornersAll);

            int newIndex = itemPositions.FindIndex(x => ImGui.IsMouseHoveringRect(x.TopLeft, x.BottomRight, true));
            if (newIndex >= 0 && oldIndex != newIndex)
            {
                itemToAdd = priorityQuests.Single(x => x.Id == _draggedItem);
                indexToAdd = newIndex;
            }
        }

        if (itemToRemove != null)
        {
            priorityQuests.Remove(itemToRemove);
        }

        if (itemToAdd != null)
        {
            priorityQuests.Remove(itemToAdd);
            priorityQuests.Insert(indexToAdd, itemToAdd);
        }
    }

    private List<ElementId> ParseClipboardItems()
    {
        List<ElementId> clipboardItems = new List<ElementId>();
        try
        {
            string? clipboardText = GetClipboardText();
            if (clipboardText != null && clipboardText.StartsWith(ClipboardPrefix, StringComparison.InvariantCulture))
            {
                clipboardText = clipboardText.Substring(ClipboardPrefix.Length);
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(clipboardText));
                foreach (string part in text.Split(ClipboardSeparator))
                {
                    ElementId elementId = ElementId.FromString(part);
                    clipboardItems.Add(elementId);
                }
            }
        }
        catch (Exception)
        {
            clipboardItems.Clear();
        }

        return clipboardItems;
    }

    private void ExportToClipboard()
    {
        string clipboardText = ClipboardPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Join(ClipboardSeparator, _questController.ManualPriorityQuests.Select(x => x.Id.ToString()))));
        ImGui.SetClipboardText(clipboardText);
        _chatGui.Print("Copied quests to clipboard.", CommandHandler.MessageTag, CommandHandler.TagColor);
    }

    private void ImportFromClipboard(List<ElementId> clipboardItems)
    {
        foreach (ElementId elementId in clipboardItems)
        {
            if (_questRegistry.TryGetQuest(elementId, out Quest? quest) &&
                !_questController.ManualPriorityQuests.Contains(quest))
                _questController.ManualPriorityQuests.Add(quest);
        }
    }

    /// <summary>
    /// The default implementation for <see cref="ImGui.GetClipboardText"/> throws an NullReferenceException if the clipboard is empty, maybe also if it doesn't contain text.
    /// </summary>
    private unsafe string? GetClipboardText()
    {
        byte* ptr = ImGuiNative.igGetClipboardText();
        if (ptr == null)
            return null;

        int byteCount = 0;
        while (ptr[byteCount] != 0)
            ++byteCount;
        return Encoding.UTF8.GetString(ptr, byteCount);
    }
}
