﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// GroupChat.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoGen.Core;

public class GroupChat : IGroupChat
{
    private IAgent? admin;
    private List<IAgent> agents = new List<IAgent>();
    private IEnumerable<IMessage> initializeMessages = new List<IMessage>();
    private Graph? workflow = null;

    public IEnumerable<IMessage>? Messages { get; private set; }

    /// <summary>
    /// Create a group chat. The next speaker will be decided by a combination effort of the admin and the workflow.
    /// </summary>
    /// <param name="admin">admin agent. If provided, the admin will be invoked to decide the next speaker.</param>
    /// <param name="workflow">workflow of the group chat. If provided, the next speaker will be decided by the workflow.</param>
    /// <param name="members">group members.</param>
    /// <param name="initializeMessages"></param>
    public GroupChat(
        IEnumerable<IAgent> members,
        IAgent? admin = null,
        IEnumerable<IMessage>? initializeMessages = null,
        Graph? workflow = null)
    {
        this.admin = admin;
        this.agents = members.ToList();
        this.initializeMessages = initializeMessages ?? new List<IMessage>();
        this.workflow = workflow;

        this.Validation();
    }

    private void Validation()
    {
        // check if all agents has a name
        if (this.agents.Any(x => string.IsNullOrEmpty(x.Name)))
        {
            throw new Exception("All agents must have a name.");
        }

        // check if any agents has the same name
        var names = this.agents.Select(x => x.Name).ToList();
        if (names.Distinct().Count() != names.Count)
        {
            throw new Exception("All agents must have a unique name.");
        }

        // if there's a workflow
        // check if the agents in that workflow are in the group chat
        if (this.workflow != null)
        {
            var agentNamesInWorkflow = this.workflow.Transitions.Select(x => x.From.Name!).Concat(this.workflow.Transitions.Select(x => x.To.Name!)).Distinct();
            if (agentNamesInWorkflow.Any(x => !this.agents.Select(a => a.Name).Contains(x)))
            {
                throw new Exception("All agents in the workflow must be in the group chat.");
            }
        }

        // must provide one of admin or workflow
        if (this.admin == null && this.workflow == null)
        {
            throw new Exception("Must provide one of admin or workflow.");
        }
    }

    /// <summary>
    /// Select the next speaker based on the conversation history.
    /// The next speaker will be decided by a combination effort of the admin and the workflow.
    /// Firstly, a group of candidates will be selected by the workflow. If there's only one candidate, then that candidate will be the next speaker.
    /// Otherwise, the admin will be invoked to decide the next speaker using role-play prompt.
    /// </summary>
    /// <param name="currentSpeaker">current speaker</param>
    /// <param name="conversationHistory">conversation history</param>
    /// <returns>next speaker.</returns>
    public async Task<IAgent?> SelectNextSpeakerAsync(IAgent currentSpeaker, IEnumerable<IMessage> conversationHistory)
    {
        var agentNames = this.agents.Select(x => x.Name).ToList();
        if (this.workflow != null)
        {
            var nextAvailableAgents = await this.workflow.TransitToNextAvailableAgentsAsync(currentSpeaker, conversationHistory);
            agentNames = nextAvailableAgents.Select(x => x.Name).ToList();
            if (agentNames.Count == 0)
            {
                throw new Exception("No next available agents found in the current workflow");
            }

            if (agentNames.Count == 1)
            {
                return this.agents.FirstOrDefault(x => x.Name == agentNames.First());
            }
        }

        if (this.admin == null)
        {
            throw new Exception("No admin is provided.");
        }

        var systemMessage = new TextMessage(Role.System,
            content: $@"You are in a role play game. Carefully read the conversation history and carry on the conversation.
The available roles are:
{string.Join(",", agentNames)}

Each message will start with 'From name:', e.g:
From {agentNames.First()}:
//your message//.

Your response should similarly identify the the next role to speak, and what they say next.

If the role play scenario is complete, your reply should include the text: {GroupChatExtension.TERMINATE}.");

        var conv = this.ProcessConversationsForRolePlay(this.initializeMessages, conversationHistory);

        var messages = new IMessage[] { systemMessage }.Concat(conv);

        var remainingAttempts = 3;
        do
        {
            try
            {
                var nextSpeaker = await TryRolePlayNextAgentAsync(admin, this.agents, messages);
                if (nextSpeaker != null)
                {
                    return nextSpeaker;
                }
            }
            catch (EndOfStreamException)
            {
                return null;
            }
            catch (InvalidOperationException) when (remainingAttempts > 1)
            {
                //Try again
            }
        } while (--remainingAttempts > 0);

        return null;
    }

    private static async Task<IAgent?> TryRolePlayNextAgentAsync(
        IAgent admin,
        IEnumerable<IAgent> availableAgents,
        IEnumerable<IMessage> messages
    )
    {
        var response = await admin.GenerateReplyAsync(
            messages: messages,
            options: new GenerateReplyOptions
            {
                Temperature = 0,
                MaxToken = 128,
                StopSequence = [":"],
                Functions = [],
            });

        if (response?.IsGroupChatTerminateMessage() ?? false)
        {
            throw new EndOfStreamException();
        }

        // remove From:
        string? name = response?.GetContent()?.Substring(5);
        if (name == null)
        {
            return null;
        }

        return availableAgents.FirstOrDefault(x =>
                x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)
            ) ?? throw new InvalidOperationException($"No member of the chat named {name}.");
    }

    /// <inheritdoc />
    public void AddInitializeMessage(IMessage message)
    {
        this.SendIntroduction(message);
    }

    public async Task<IEnumerable<IMessage>> CallAsync(
        IEnumerable<IMessage>? conversationWithName = null,
        int maxRound = 10,
        CancellationToken ct = default)
    {
        var conversationHistory = new List<IMessage>();
        if (conversationWithName != null)
        {
            conversationHistory.AddRange(conversationWithName);
        }

        var lastSpeakerName = conversationHistory.Last()?.From;
        var lastSpeaker = lastSpeakerName switch
        {
            null => this.agents.First(),
            _ => this.agents.FirstOrDefault(x => x.Name == lastSpeakerName) ?? throw new Exception($"The agent '{lastSpeakerName}' is not in the group chat"),
        };

        int round = 0;
        while (round < maxRound)
        {
            IMessage? result = null;
            try
            {
                var currentSpeaker = await this.SelectNextSpeakerAsync(lastSpeaker, conversationHistory);
                if (currentSpeaker == null)
                {
                    // if no next speaker was identified, terminate the conversation
                    break;
                }

                var processedConversation = this.ProcessConversationForAgent(this.initializeMessages, conversationHistory);

                var remainingRequestFailedExceptions = 3;
                do
                {
                    try
                    {
                        result = await currentSpeaker.GenerateReplyAsync(processedConversation, cancellationToken: ct);
                    }
                    catch (Exception ex)
                        when (ex.GetType().FullName == "Azure.RequestFailedException" && --remainingRequestFailedExceptions > 0)
                    {
                        //Try again
                    }
                } while (result == null);

                lastSpeaker = currentSpeaker;
            }
            catch (Exception ex)
            {
                result = new TextMessage(
                    Role.Assistant,
                    $"{ex}{Environment.NewLine}{Environment.NewLine}{GroupChatExtension.TERMINATE}",
                    from: this.admin?.Name);
            }

            conversationHistory.Add(result);

            // if message is terminate message, then terminate the conversation
            if (result?.IsGroupChatTerminateMessage() ?? false)
            {
                break;
            }

            round++;
        }

        return conversationHistory;
    }

    public void SendIntroduction(IMessage message)
    {
        this.initializeMessages = this.initializeMessages.Append(message);
    }
}
