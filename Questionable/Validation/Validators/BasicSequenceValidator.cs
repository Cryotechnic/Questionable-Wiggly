﻿using System.Collections.Generic;
using System.Linq;
using Questionable.Model;
using Questionable.Model.V1;

namespace Questionable.Validation.Validators;

internal sealed class BasicSequenceValidator : IQuestValidator
{
    /// <summary>
    /// A quest should have sequences from 0 to N, and (if more than 'AcceptQuest' exists), a 255 sequence.
    /// </summary>
    public IEnumerable<ValidationIssue> Validate(Quest quest)
    {
        var sequences = quest.Root.QuestSequence;
        var foundStart = sequences.FirstOrDefault(x => x.Sequence == 0);
        if (foundStart == null)
        {
            yield return new ValidationIssue
            {
                QuestId = quest.QuestId,
                Sequence = 0,
                Step = null,
                Severity = EIssueSeverity.Error,
                Description = "Missing quest start",
            };
            yield break;
        }

        if (quest.Info.CompletesInstantly)
        {
            foreach (var sequence in sequences)
            {
                if (sequence == foundStart)
                    continue;

                yield return new ValidationIssue
                {
                    QuestId = quest.QuestId,
                    Sequence = (byte)sequence.Sequence,
                    Step = null,
                    Severity = EIssueSeverity.Error,
                    Description = "Instant quest should not have any sequences after the start",
                };
            }
        }
        else
        {
            int maxSequence = sequences.Select(x => x.Sequence)
                .Where(x => x != 255)
                .Max();

            for (int i = 0; i < maxSequence; i++)
            {
                var foundSequences = sequences.Where(x => x.Sequence == i).ToList();
                var issue = ValidateSequences(quest, i, foundSequences);
                if (issue != null)
                    yield return issue;
            }

            var foundEnding = sequences.Where(x => x.Sequence == 255).ToList();
            var endingIssue = ValidateSequences(quest, 255, foundEnding);
            if (endingIssue != null)
                yield return endingIssue;
        }
    }

    private static ValidationIssue? ValidateSequences(Quest quest, int sequenceNo, List<QuestSequence> foundSequences)
    {
        if (foundSequences.Count == 0)
        {
            return new ValidationIssue
            {
                QuestId = quest.QuestId,
                Sequence = (byte)sequenceNo,
                Step = null,
                Severity = EIssueSeverity.Error,
                Description = "Missing sequence",
            };
        }
        else if (foundSequences.Count == 2)
        {
            return new ValidationIssue
            {
                QuestId = quest.QuestId,
                Sequence = (byte)sequenceNo,
                Step = null,
                Severity = EIssueSeverity.Error,
                Description = "Duplicate sequence",
            };
        }
        else
            return null;
    }
}
