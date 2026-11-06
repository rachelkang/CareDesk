using System.Globalization;
using System.IO;
using CareDesk.Core;
using CareDesk.Desktop;
using CareDesk.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CareDesk.Tests;

public sealed class MessagingWorkflowTests
{
    private static readonly TimeZoneInfo ClinicZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Conversation_request_and_appointment_link_by_id_and_reject_unrelated_context()
    {
        await WithWorkflow(async context =>
        {
            var inbox = await context.Workflow.GetInboxAsync();
            var mads = Assert.Single(inbox, item => item.Conversation.Id == MessagingSeedData.MadsConversationId);
            var details = await context.Workflow.GetConversationAsync(mads.Conversation.Id);
            Assert.NotNull(details);
            Assert.Equal(mads.Request.Id, details!.Request.Id);
            Assert.Equal(details.Request.AppointmentId, details.Appointment!.Id);
            Assert.Equal(details.Conversation.PatientId, details.Appointment.PatientId);

            var unrelated = (await context.Store.GetAppointmentsAsync(default)).Single(a => a.PatientName == "Harshada Hole");
            Assert.Null(await context.Workflow.GetRequestForAppointmentAsync(unrelated.Id, details.Request.Id));
            Assert.Null(await context.Workflow.GetRequestForAppointmentAsync(details.Appointment.Id, MessagingSeedData.AnsonRequestId));
        });
    }

    [Fact]
    public async Task Delayed_reply_suggestion_cannot_overwrite_a_newer_proposal()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var replyService = new DelayedReplySuggestionService();
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [replyService, replyService]);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);

            replyService.DelayNext = true;
            schedule.ProposedTime = "11:00 AM";
            var older = schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            await replyService.Started.Task;
            schedule.ProposedTime = "12:00 PM";
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            replyService.Release.SetResult(ReplySuggestionResult.Success("Older 11:00 AM result", "Delayed AI"));
            await older;

            Assert.Contains("12:00 PM", schedule.SuggestedReplyText);
            Assert.DoesNotContain("Older", schedule.SuggestedReplyText);
        });
    }

    [Fact]
    public async Task Unconfigured_live_reply_suggestion_fails_without_network_or_false_success()
    {
        var service = new OpenAiReplySuggestionService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<OpenAiReplySuggestionService>.Instance);
        var appointment = SeedData.ForDay(new DateOnly(2026, 1, 5), new ClinicClock(ClinicZone)).Single(value => value.PatientName == "Mads Kristensen");
        var result = await service.PrepareAsync(new(appointment, SeedData.ReschedulingMessage, new DateTime(2026, 1, 5, 10, 0, 0), appointment.ClinicianName, 30, false, ClinicZone.Id), default);
        Assert.False(result.Succeeded);
        Assert.Contains("not configured", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delayed_conversation_load_cannot_replace_a_newer_patient_selection()
    {
        await WithWorkflow(async context =>
        {
            var delayedStore = new DelayedWorkflowStore(context.Store);
            var workflow = CreateWorkflow(delayedStore, context.Scheduling, context.Clock, context.TimeProvider, MessagingSeedData.CurrentStaff);
            var composer = new ReplyComposerViewModel(workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var messages = new MessagesViewModel(workflow, composer, context.Clock, NullLogger<MessagesViewModel>.Instance);
            await messages.InitializeAsync(default);

            delayedStore.DelayConversationId = MessagingSeedData.AnsonConversationId;
            var olderSelection = messages.SelectConversationAsync(MessagingSeedData.AnsonConversationId);
            await delayedStore.Started.Task;
            Assert.True(await messages.SelectConversationAsync(MessagingSeedData.JasonConversationId));
            delayedStore.Release.SetResult();
            await olderSelection;

            Assert.Equal(MessagingSeedData.JasonConversationId, messages.Details!.Conversation.Id);
            Assert.NotEqual(MessagingSeedData.AnsonRequestId, messages.Composer.Details?.Request.Id);
        });
    }

    [Fact]
    public async Task One_saved_draft_is_shared_persisted_and_not_silently_replaced()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var manual = await context.Workflow.OpenManualDraftAsync(details);
            Assert.True(manual.Succeeded, manual.Error);
            var saved = await context.Workflow.SaveDraftAsync(details.Request, manual.Value!, "Which times work for you tomorrow?");
            Assert.True(saved.Succeeded, saved.Error);

            var reopenedStore = new SqliteAppointmentStore(context.Path, context.Clock, context.TimeProvider);
            await reopenedStore.InitializeAsync(default);
            var reopenedService = CreateWorkflow(reopenedStore, context.Scheduling, context.Clock, context.TimeProvider, MessagingSeedData.CurrentStaff);
            var reopened = await reopenedService.GetConversationAsync(details.Conversation.Id);
            Assert.Equal(saved.Value!.Id, reopened!.Draft!.Id);
            Assert.Equal("Which times work for you tomorrow?", reopened.Draft.Body);
            Assert.True(reopened.Draft.HasStaffEdits);
            Assert.Single(await reopenedService.GetInboxAsync(), item => item.Request.Id == details.Request.Id && item.Draft is not null);
        });
    }

    [Fact]
    public async Task Appointment_commit_moves_to_reply_pending_and_confirmation_uses_saved_facts()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var appointment = details.Appointment!;
            var local = context.Clock.ToLocal(appointment.StartUtc).AddHours(2);
            var result = await context.Workflow.RescheduleAsync(details.Request, new(appointment.Id, appointment.ClinicianId, local, 30));
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(SchedulingWorkStatus.ReplyPending, result.Value!.Request.Status);
            Assert.Equal(appointment.Revision + 1, result.Value.Appointment!.Revision);

            var pending = await context.Workflow.GetConversationAsync(details.Conversation.Id);
            Assert.Single(pending!.Messages);
            Assert.Null(pending.Draft);
            var draft = await context.Workflow.OpenConfirmationDraftAsync(pending);
            Assert.True(draft.Succeeded, draft.Error);
            Assert.Contains(local.ToString("h:mm tt", CultureInfo.CurrentCulture), draft.Value!.Body);
            Assert.Contains(result.Value.Appointment.ClinicianName, draft.Value.Body);
            Assert.Equal(result.Value.Appointment.Revision, draft.Value.AppointmentRevision);
        });
    }

    [Fact]
    public async Task Later_appointment_revision_marks_confirmation_stale_and_requires_explicit_refresh()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var firstLocal = context.Clock.ToLocal(details.Appointment!.StartUtc).AddHours(1);
            var first = await context.Workflow.RescheduleAsync(details.Request, new(details.Appointment.Id, details.Appointment.ClinicianId, firstLocal, 30));
            Assert.True(first.Succeeded, first.Error);
            var pending = await context.Workflow.GetConversationAsync(details.Conversation.Id);
            var generated = await context.Workflow.OpenConfirmationDraftAsync(pending!);
            var edited = await context.Workflow.SaveDraftAsync(pending!.Request, generated.Value!, generated.Value!.Body + " Please arrive ten minutes early.");
            Assert.True(edited.Succeeded, edited.Error);

            var secondContext = await context.Workflow.GetConversationAsync(details.Conversation.Id);
            var secondLocal = context.Clock.ToLocal(secondContext!.Appointment!.StartUtc).AddHours(1);
            var second = await context.Workflow.RescheduleAsync(secondContext.Request, new(secondContext.Appointment.Id, secondContext.Appointment.ClinicianId, secondLocal, 30));
            Assert.True(second.Succeeded, second.Error);
            var stale = await context.Workflow.GetConversationAsync(details.Conversation.Id);
            Assert.True(stale!.Draft!.IsStaleFor(stale.Appointment));
            Assert.False((await context.Workflow.RecordReplyAsync(stale, SchedulingWorkStatus.Completed)).Succeeded);
            var protectedRefresh = await context.Workflow.OpenConfirmationDraftAsync(stale);
            Assert.False(protectedRefresh.Succeeded);
            Assert.Contains("staff edits", protectedRefresh.Error!, StringComparison.OrdinalIgnoreCase);
            var refreshed = await context.Workflow.OpenConfirmationDraftAsync(stale, replaceEditedStaleDraft: true);
            Assert.True(refreshed.Succeeded, refreshed.Error);
            Assert.False(refreshed.Value!.IsStaleFor(stale.Appointment));
            Assert.DoesNotContain("arrive ten minutes", refreshed.Value.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Ownership_read_tracking_and_stale_versions_do_not_mutate_work_silently()
    {
        await WithWorkflow(async context =>
        {
            var original = (await context.Workflow.GetConversationAsync(MessagingSeedData.MadsConversationId))!;
            await context.Workflow.MarkReadAsync(original.Conversation.Id);
            var afterRead = (await context.Workflow.GetConversationAsync(original.Conversation.Id))!;
            Assert.Null(afterRead.Request.OwnerId);
            Assert.Equal(SchedulingWorkStatus.Open, afterRead.Request.Status);

            var claimed = await context.Workflow.AssignToMeAsync(afterRead.Request);
            Assert.True(claimed.Succeeded, claimed.Error);
            var otherActor = CreateWorkflow(context.Store, context.Scheduling, context.Clock, context.TimeProvider, MessagingSeedData.SecondStaff);
            var staleClaim = await otherActor.AssignToMeAsync(original.Request);
            Assert.False(staleClaim.Succeeded);
            Assert.Contains("changed", staleClaim.Error!, StringComparison.OrdinalIgnoreCase);

            var owned = (await context.Workflow.GetConversationAsync(original.Conversation.Id))!;
            var denied = await otherActor.OpenManualDraftAsync(owned);
            Assert.False(denied.Succeeded);
            Assert.Contains(MessagingSeedData.CurrentStaff.DisplayName, denied.Error!);

            var released = await context.Workflow.ReleaseAsync(owned.Request);
            Assert.True(released.Succeeded, released.Error);
            var openAgain = (await context.Workflow.GetConversationAsync(original.Conversation.Id))!;
            Assert.Null(openAgain.Request.OwnerId);
            Assert.Equal(SchedulingWorkStatus.Open, openAgain.Request.Status);
            var reclaimed = await context.Workflow.AssignToMeAsync(openAgain.Request);
            Assert.True(reclaimed.Succeeded, reclaimed.Error);
            var currentAgain = (await context.Workflow.GetConversationAsync(original.Conversation.Id))!;
            var reassigned = await context.Workflow.ReassignAsync(currentAgain.Request, MessagingSeedData.SecondStaff.Id);
            Assert.True(reassigned.Succeeded, reassigned.Error);
            var assignedElsewhere = (await context.Workflow.GetConversationAsync(original.Conversation.Id))!;
            Assert.Equal(MessagingSeedData.SecondStaff.Id, assignedElsewhere.Request.OwnerId);
            Assert.Equal(MessagingSeedData.SecondStaff.DisplayName, assignedElsewhere.OwnerDisplayName);

            var direct = (await context.Store.GetAppointmentsAsync(default)).Single(a => a.PatientName == "Harshada Hole");
            var directLocal = context.Clock.ToLocal(direct.StartUtc).AddHours(1);
            var directResult = await context.Scheduling.RescheduleAsync(new(direct.Id, direct.ClinicianId, directLocal, 30));
            Assert.True(directResult.Succeeded, directResult.Error);
        });
    }

    [Fact]
    public async Task Validation_failure_leaves_request_appointment_and_history_unchanged()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var before = details.Appointment!;
            var result = await context.Workflow.RescheduleAsync(details.Request, new(before.Id, before.ClinicianId, new DateTime(2025, 12, 31, 10, 0, 0), 30));
            Assert.False(result.Succeeded);
            var after = await context.Workflow.GetConversationAsync(details.Conversation.Id);
            Assert.Equal(before, after!.Appointment);
            Assert.Equal(SchedulingWorkStatus.InProgress, after.Request.Status);
            Assert.Null(after.Request.OutcomeAppointmentRevision);
            Assert.Null(after.Draft);
            Assert.Single(after.Messages);
        });
    }

    [Fact]
    public async Task Recording_reply_is_atomic_idempotent_and_does_not_change_appointment()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var appointment = details.Appointment!;
            var draft = await context.Workflow.OpenManualDraftAsync(details);
            var saved = await context.Workflow.SaveDraftAsync(details.Request, draft.Value!, "Please tell us which times work best.");
            var ready = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            var first = await context.Workflow.RecordReplyAsync(ready, SchedulingWorkStatus.WaitingOnPatient);
            Assert.True(first.Succeeded, first.Error);
            var second = await context.Workflow.RecordReplyAsync(ready, SchedulingWorkStatus.WaitingOnPatient);
            Assert.True(second.Succeeded, second.Error);

            var after = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(SchedulingWorkStatus.WaitingOnPatient, after.Request.Status);
            Assert.Null(after.Draft);
            Assert.Single(after.Messages, message => message.Direction == MessageDirection.Outgoing);
            Assert.Equal("Recorded locally · not delivered", after.Messages.Single(message => message.Direction == MessageDirection.Outgoing).RecordStatus);
            Assert.Equal(appointment, after.Appointment);
            Assert.Equal(saved.Value!.Id.Value, after.Messages.Single(message => message.Direction == MessageDirection.Outgoing).SourceDraftId!.Value.Value);
        });
    }

    [Fact]
    public async Task Draft_or_reply_storage_failure_preserves_committed_or_unsent_state()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var local = context.Clock.ToLocal(details.Appointment!.StartUtc).AddHours(1);
            var committed = await context.Workflow.RescheduleAsync(details.Request, new(details.Appointment.Id, details.Appointment.ClinicianId, local, 30));
            Assert.True(committed.Succeeded, committed.Error);

            var faultingStore = new FaultingWorkflowStore(context.Store) { FailDraftSave = true };
            var faultingWorkflow = CreateWorkflow(faultingStore, context.Scheduling, context.Clock, context.TimeProvider, MessagingSeedData.CurrentStaff);
            var pending = (await faultingWorkflow.GetConversationAsync(details.Conversation.Id))!;
            await Assert.ThrowsAsync<IOException>(() => faultingWorkflow.OpenConfirmationDraftAsync(pending));
            var afterDraftFailure = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(SchedulingWorkStatus.ReplyPending, afterDraftFailure.Request.Status);
            Assert.Equal(local, context.Clock.ToLocal(afterDraftFailure.Appointment!.StartUtc));
            Assert.Null(afterDraftFailure.Draft);

            faultingStore.FailDraftSave = false;
            var manual = await context.Workflow.OpenManualDraftAsync(afterDraftFailure);
            var saved = await context.Workflow.SaveDraftAsync(afterDraftFailure.Request, manual.Value!, "We recorded the appointment change.");
            Assert.True(saved.Succeeded, saved.Error);
            faultingStore.FailReplyRecord = true;
            var ready = (await faultingWorkflow.GetConversationAsync(details.Conversation.Id))!;
            await Assert.ThrowsAsync<IOException>(() => faultingWorkflow.RecordReplyAsync(ready, SchedulingWorkStatus.Completed));
            var afterReplyFailure = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(SchedulingWorkStatus.ReplyPending, afterReplyFailure.Request.Status);
            Assert.NotNull(afterReplyFailure.Draft);
            Assert.DoesNotContain(afterReplyFailure.Messages, message => message.Direction == MessageDirection.Outgoing);
        });
    }

    [Fact]
    public async Task Schema_seed_and_reset_are_idempotent_and_restore_coherent_relationships()
    {
        await WithWorkflow(async context =>
        {
            await context.Store.InitializeAsync(default);
            Assert.Equal(3, (await context.Workflow.GetInboxAsync()).Count);
            var details = await ClaimMadsAsync(context.Workflow);
            var draft = await context.Workflow.OpenManualDraftAsync(details);
            Assert.True(draft.Succeeded, draft.Error);

            await context.Scheduling.ResetAsync();
            var resetInbox = await context.Workflow.GetInboxAsync();
            Assert.Equal(3, resetInbox.Count);
            var reset = (await context.Workflow.GetConversationAsync(MessagingSeedData.MadsConversationId))!;
            Assert.Equal(SchedulingWorkStatus.Open, reset.Request.Status);
            Assert.Null(reset.Request.OwnerId);
            Assert.Null(reset.Draft);
            Assert.Equal(reset.Request.AppointmentId, reset.Appointment!.Id);
            Assert.Equal(SeedData.ReschedulingMessage, reset.Messages.Single().Body);
        });
    }

    [Fact]
    public async Task Shell_navigation_keeps_the_same_request_and_draft_in_both_sections()
    {
        await WithWorkflow(async context =>
        {
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var messages = new MessagesViewModel(context.Workflow, composer, context.Clock, NullLogger<MessagesViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [new SampleReplySuggestionService(), new SampleReplySuggestionService()]);
            var shell = new ShellViewModel(messages, schedule, composer);
            await shell.InitializeAsync(default);
            Assert.Equal(MessagingSeedData.MadsConversationId, messages.Details!.Conversation.Id);
            await messages.AssignToMeCommand.ExecuteAsync(null);
            await messages.StartReplyCommand.ExecuteAsync(null);
            composer.Text = "Which times work for you?";
            Assert.True(await composer.FlushAsync());
            await messages.ReloadSelectedAsync();
            Assert.NotNull(messages.Details!.Appointment);

            await shell.OpenLinkedAppointmentCommand.ExecuteAsync(null);
            Assert.Equal("Schedule", shell.ActiveSection);
            Assert.Equal(messages.Details!.Request.Id, schedule.LinkedContext!.Request.Id);
            Assert.Equal("Which times work for you?", composer.Text);
            await shell.OpenConversationCommand.ExecuteAsync(null);
            Assert.Equal("Messages", shell.ActiveSection);
            Assert.Equal(schedule.LinkedContext!.Conversation.Id, messages.Details!.Conversation.Id);
            Assert.Equal("Which times work for you?", messages.Composer.Text);
            await messages.RecordAndCompleteCommand.ExecuteAsync(null);
            await shell.ShowScheduleCommand.ExecuteAsync(null);
            Assert.Equal(SchedulingWorkStatus.Completed, schedule.LinkedContext!.Request.Status);
        });
    }

    [Fact]
    public async Task Draft_save_failure_retains_local_text_and_blocks_context_switch_flush()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var draft = await context.Workflow.OpenManualDraftAsync(details);
            var reloaded = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            var faulting = new FaultingWorkflowStore(context.Store) { FailDraftSave = true };
            var workflow = CreateWorkflow(faulting, context.Scheduling, context.Clock, context.TimeProvider, MessagingSeedData.CurrentStaff);
            var composer = new ReplyComposerViewModel(workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            Assert.True(await composer.LoadAsync(reloaded));
            composer.Text = "Keep this unsaved text";
            Assert.False(await composer.FlushAsync());
            Assert.Equal("Keep this unsaved text", composer.Text);
            Assert.True(composer.IsDirty);
            Assert.Contains("still here", composer.ErrorText!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Updated_sample_focus_roster_and_consolidated_suggestions_are_coherent()
    {
        await WithWorkflow(async context =>
        {
            Assert.Equal(["Rachel Kang", "Barbara Boyajian"], MessagingSeedData.Staff.Select(member => member.DisplayName));
            Assert.Contains(await context.Store.GetCliniciansAsync(default), clinician => clinician.DisplayName == "Dr. Andy Sterland");
            var appointments = await context.Store.GetAppointmentsAsync(default);
            var mads = appointments.Single(appointment => appointment.PatientName == "Mads Kristensen");
            Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(mads.StartUtc)));
            Assert.Equal(SeedData.ReschedulingMessage, mads.PatientRequest);
            Assert.Null(appointments.Single(appointment => appointment.PatientName == "Harshada Hole").PatientRequest);

            var details = await ClaimMadsAsync(context.Workflow);
            var suggested = await context.Workflow.OpenSuggestedManualDraftAsync(details, "We can offer 10:00 AM. Would that work for you?");
            Assert.True(suggested.Succeeded, suggested.Error);
            var repeated = await context.Workflow.OpenSuggestedManualDraftAsync(details, "Replacement text");
            Assert.Equal(suggested.Value!.Id, repeated.Value!.Id);
            Assert.Equal("We can offer 10:00 AM. Would that work for you?", repeated.Value.Body);

            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [new SampleReplySuggestionService(), new SampleReplySuggestionService()]);
            await schedule.InitializeAsync(default);
            Assert.Equal("Mads Kristensen", schedule.CurrentPatient);
            Assert.True(await schedule.OpenAppointmentAsync(mads.Id, MessagingSeedData.MadsRequestId));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            Assert.True(schedule.HasAppointmentSuggestion);
            Assert.Equal("Reply to patient suggesting a different time", schedule.ReplySuggestionTitle);
            Assert.Contains("10:00 AM", schedule.SuggestedReplyText);
            schedule.RejectAppointmentSuggestionCommand.Execute(null);
            Assert.True(schedule.HasAppointmentSuggestion);
            Assert.False(schedule.IsAppointmentSuggestionActive);
            Assert.Equal("REJECTED", schedule.AppointmentActionOutcome);
            Assert.False(schedule.CanAcceptSuggestedUpdate);
            schedule.RejectReplySuggestionCommand.Execute(null);
            Assert.True(schedule.HasReplySuggestion);
            Assert.False(schedule.IsReplySuggestionActive);
            Assert.Equal("REJECTED", schedule.ReplyActionOutcome);
            Assert.False(schedule.CanUseReplySuggestion);
            await schedule.RefreshAgendaCommand.ExecuteAsync(null);
            await schedule.RefreshLinkedContextAsync();
            Assert.Equal("REJECTED", schedule.AppointmentActionOutcome);
            Assert.Equal("REJECTED", schedule.ReplyActionOutcome);
            Assert.True(schedule.HasRequestAssistance);
        });
    }

    [Fact]
    public async Task Reply_suggestion_uses_patient_message_tracks_schedule_and_records_the_current_workflow_state()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var replyService = new CapturingReplySuggestionService();
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [replyService, replyService]);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            Assert.Equal(SeedData.ReschedulingMessage, replyService.Requests[^1].Message);
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(replyService.Requests[^1].ProposedLocalStart));
            Assert.False(replyService.Requests[^1].IsCommitted);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);

            schedule.ProposedTime = "11:00 AM";
            Assert.True(schedule.CanSaveChanges);
            Assert.False(schedule.IsManualAppointmentEdit);

            schedule.BeginManualUpdateCommand.Execute(null);
            Assert.Equal("EDITING", schedule.AppointmentActionOutcome);
            Assert.False(schedule.IsAppointmentSuggestionActive);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            schedule.ProposedTime = "11:00 AM";
            Assert.True(schedule.CanSaveChanges);
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            Assert.Equal(new TimeOnly(11, 0), TimeOnly.FromDateTime(replyService.Requests[^1].ProposedLocalStart));
            Assert.Contains("11:00 AM", schedule.SuggestedReplyText);

            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);
            Assert.Equal(SchedulingWorkStatus.ReplyPending, schedule.LinkedContext!.Request.Status);
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(schedule.LinkedContext.Appointment!.StartUtc)));
            Assert.False(schedule.IsDirty);
            Assert.False(schedule.IsManualAppointmentEdit);
            Assert.False(schedule.CanSaveChanges);
            Assert.Equal("UPDATED", schedule.AppointmentActionOutcome);
            Assert.True(schedule.HasAppointmentSuggestion);
            Assert.False(schedule.IsAppointmentSuggestionActive);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.True(schedule.IsReplySuggestionActive);
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            Assert.True(replyService.Requests[^1].IsCommitted);
            Assert.Contains("saved", schedule.SuggestedReplyText, StringComparison.OrdinalIgnoreCase);

            await schedule.RecordSuggestedReplyCommand.ExecuteAsync(null);
            Assert.Equal(SchedulingWorkStatus.Completed, schedule.LinkedContext!.Request.Status);
            Assert.Single(schedule.LinkedContext.Messages, message => message.Direction == MessageDirection.Outgoing);
            Assert.Equal("RECORDED", schedule.ReplyActionOutcome);
            Assert.True(schedule.HasReplySuggestion);
            Assert.False(schedule.IsReplySuggestionActive);
            Assert.False(schedule.CanUseReplySuggestion);
            await schedule.RecordSuggestedReplyCommand.ExecuteAsync(null);
            Assert.Contains("already been acted on", schedule.ErrorText);
            Assert.Single((await context.Workflow.GetConversationAsync(details.Conversation.Id))!.Messages, message => message.Direction == MessageDirection.Outgoing);
        });
    }

    [Fact]
    public async Task Draft_handoff_collapses_suggestion_but_preserves_editing_and_recording()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [new SampleReplySuggestionService(), new SampleReplySuggestionService()]);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            await schedule.EditSuggestedReplyCommand.ExecuteAsync(null);
            Assert.Equal("DRAFT", schedule.ReplyActionOutcome);
            Assert.False(schedule.IsReplySuggestionActive);
            Assert.True(schedule.HasReplySuggestion);
            Assert.True(composer.HasDraft);
            Assert.True(composer.IsEditable);
            Assert.True(composer.CanRecord);
            composer.Text = "Staff-reviewed reply";
            await schedule.RecordReplyDraftCommand.ExecuteAsync(null);
            Assert.Equal("RECORDED", schedule.ReplyActionOutcome);
            Assert.False(composer.HasDraft);
            var outgoing = Assert.Single(schedule.LinkedContext!.Messages, message => message.Direction == MessageDirection.Outgoing);
            Assert.Equal("Staff-reviewed reply", outgoing.Body);
            Assert.Equal(SchedulingWorkStatus.WaitingOnPatient, schedule.LinkedContext.Request.Status);
            Assert.True(schedule.IsAppointmentSuggestionActive);
        });
    }

    [Fact]
    public async Task Failed_suggested_update_stays_active_and_rejection_cancels_late_reply()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var replyService = new DelayedReplySuggestionService();
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [replyService, replyService]);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            await schedule.ReleaseRequestCommand.ExecuteAsync(null);
            Assert.False(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);
            await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);
            Assert.NotNull(schedule.ErrorText);
            Assert.True(schedule.IsAppointmentSuggestionActive);
            Assert.Empty(schedule.AppointmentActionOutcome);
            Assert.Equal(details.Appointment.StartUtc, schedule.LinkedContext!.Appointment!.StartUtc);

            replyService.DelayNext = true;
            var pending = schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            await replyService.Started.Task;
            schedule.RejectReplySuggestionCommand.Execute(null);
            replyService.Release.SetResult(ReplySuggestionResult.Success("Late suggestion", "Delayed AI"));
            await pending;
            Assert.Equal("REJECTED", schedule.ReplyActionOutcome);
            Assert.False(schedule.IsReplySuggestionActive);
            Assert.False(schedule.IsPreparingReplySuggestion);
            Assert.DoesNotContain("Late suggestion", schedule.SuggestedReplyText);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_save_remains_available_after_suggestion_is_rejected_or_updated(bool acceptSuggestion)
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [new SampleReplySuggestionService(), new SampleReplySuggestionService()]);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            if (acceptSuggestion) await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);
            else schedule.RejectAppointmentSuggestionCommand.Execute(null);

            Assert.Equal(acceptSuggestion ? "UPDATED" : "REJECTED", schedule.AppointmentActionOutcome);
            Assert.True(schedule.HasAppointmentSuggestion);
            Assert.False(schedule.IsAppointmentSuggestionActive);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);
            Assert.False(schedule.IsManualAppointmentEdit);

            schedule.ProposedTime = "11:00 AM";
            Assert.True(schedule.CanSaveChanges);
            Assert.False(schedule.IsManualAppointmentEdit);
            await schedule.ConfirmRescheduleCommand.ExecuteAsync(null);

            Assert.Null(schedule.ErrorText);
            Assert.Equal("UPDATED", schedule.AppointmentActionOutcome);
            Assert.False(schedule.IsDirty);
            Assert.False(schedule.CanSaveChanges);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            var saved = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(new TimeOnly(11, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(saved.Appointment!.StartUtc)));
            Assert.Equal(SchedulingWorkStatus.ReplyPending, saved.Request.Status);
            Assert.Empty(saved.Messages.Where(message => message.Direction == MessageDirection.Outgoing));

            await schedule.RefreshAgendaCommand.ExecuteAsync(null);
            await schedule.RefreshLinkedContextAsync();
            Assert.Equal("UPDATED", schedule.AppointmentActionOutcome);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);
        });
    }

    [Fact]
    public async Task Direct_manual_edits_enable_save_and_reverting_disables_it_without_suggestion_handoff()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, null);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            var originalTime = schedule.ProposedTime;
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);

            schedule.ProposedTime = "11:00 AM";
            Assert.True(schedule.CanSaveChanges);
            Assert.False(schedule.IsManualAppointmentEdit);
            schedule.ProposedTime = originalTime;
            Assert.False(schedule.IsDirty);
            Assert.False(schedule.CanSaveChanges);

            schedule.ProposedTime = "11:00 AM";
            await schedule.ConfirmRescheduleCommand.ExecuteAsync(null);
            Assert.Null(schedule.ErrorText);
            var saved = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(new TimeOnly(11, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(saved.Appointment!.StartUtc)));
            Assert.Equal(details.Appointment.Id, saved.Appointment.Id);
            Assert.Equal(SchedulingWorkStatus.ReplyPending, saved.Request.Status);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);
        });
    }

    [Fact]
    public async Task Always_available_manual_editor_still_requires_selection_and_request_ownership()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, null);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            schedule.ProposedTime = "11:00 AM";
            Assert.True(schedule.CanSaveChanges);

            await schedule.ReleaseRequestCommand.ExecuteAsync(null);
            Assert.False(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);
            await schedule.ConfirmRescheduleCommand.ExecuteAsync(null);
            Assert.NotNull(schedule.ErrorText);
            Assert.Equal(details.Appointment.StartUtc, (await context.Workflow.GetConversationAsync(details.Conversation.Id))!.Appointment!.StartUtc);

            await schedule.AssignRequestToMeCommand.ExecuteAsync(null);
            Assert.True(schedule.AreManualAppointmentFieldsEnabled);
            Assert.True(schedule.CanSaveChanges);
            schedule.SelectedRow = null;
            Assert.False(schedule.AreManualAppointmentFieldsEnabled);
            Assert.False(schedule.CanSaveChanges);
        });
    }

    [Fact]
    public async Task Suggested_update_commits_once_without_manual_save_or_recording_a_reply()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, null);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            Assert.False(schedule.IsDirty);
            Assert.False(schedule.CanSaveChanges);
            Assert.True(schedule.CanAcceptSuggestedUpdate);

            await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);

            var saved = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(saved.Appointment!.StartUtc)));
            Assert.Equal(details.Appointment.Revision + 1, saved.Appointment.Revision);
            Assert.Equal(saved.Appointment.Revision, saved.Request.OutcomeAppointmentRevision);
            Assert.Equal(SchedulingWorkStatus.ReplyPending, saved.Request.Status);
            Assert.Null(saved.Draft);
            Assert.Empty(saved.Messages.Where(message => message.Direction == MessageDirection.Outgoing));
            Assert.Equal("UPDATED", schedule.AppointmentActionOutcome);
            Assert.False(schedule.IsBusy);
            Assert.False(schedule.IsDirty);
            Assert.False(schedule.IsManualAppointmentEdit);
            Assert.False(schedule.CanSaveChanges);
            Assert.False(schedule.CanAcceptSuggestedUpdate);

            await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);
            var repeated = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.NotNull(schedule.ErrorText);
            Assert.Equal(saved.Appointment, repeated.Appointment);
            Assert.Equal(saved.Request, repeated.Request);
            Assert.Equal(saved.Messages, repeated.Messages);
        });
    }

    [Fact]
    public async Task Update_differently_preserves_booking_until_manual_save_commits_staff_values()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, null);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);

            schedule.BeginManualUpdateCommand.Execute(null);
            schedule.ProposedTime = "11:00 AM";
            schedule.ProposedDuration = 45;
            schedule.ProposedClinician = schedule.Clinicians.Single(clinician => clinician.DisplayName == "Dr. Andy Sterland");
            Assert.True(schedule.IsManualAppointmentEdit);
            Assert.True(schedule.CanSaveChanges);
            Assert.False(schedule.CanAcceptSuggestedUpdate);
            var pending = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(details.Appointment, pending.Appointment);
            Assert.Equal(details.Request, pending.Request);
            Assert.Null(pending.Draft);

            await schedule.ConfirmRescheduleCommand.ExecuteAsync(null);

            Assert.Null(schedule.ErrorText);
            var saved = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(new TimeOnly(11, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(saved.Appointment!.StartUtc)));
            Assert.Equal(TimeSpan.FromMinutes(45), saved.Appointment.EndUtc - saved.Appointment.StartUtc);
            Assert.Equal("Dr. Andy Sterland", saved.Appointment.ClinicianName);
            Assert.Equal(details.Appointment.Revision + 1, saved.Appointment.Revision);
            Assert.Equal(SchedulingWorkStatus.ReplyPending, saved.Request.Status);
            Assert.Empty(saved.Messages.Where(message => message.Direction == MessageDirection.Outgoing));
            Assert.False(schedule.IsManualAppointmentEdit);
            Assert.False(schedule.IsDirty);
            Assert.False(schedule.CanSaveChanges);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suggested_update_failure_retains_proposal_and_retry_commits_only_once(bool staleOwnership)
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var faulting = new FaultingWorkflowStore(context.Store) { FailReschedule = !staleOwnership };
            var workflow = CreateWorkflow(faulting, context.Scheduling, context.Clock, context.TimeProvider, MessagingSeedData.CurrentStaff);
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, workflow, null);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareDraftCommand.ExecuteAsync(null);
            if (staleOwnership)
            {
                var released = await context.Workflow.ReleaseAsync(details.Request);
                Assert.True(released.Succeeded, released.Error);
            }
            var before = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;

            await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);

            Assert.NotNull(schedule.ErrorText);
            Assert.False(schedule.IsBusy);
            Assert.True(schedule.IsDirty);
            Assert.True(schedule.IsAppointmentSuggestionActive);
            Assert.Empty(schedule.AppointmentActionOutcome);
            Assert.Equal("10:00 AM", schedule.ProposedTime);
            var failed = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(before.Appointment, failed.Appointment);
            Assert.Equal(before.Request, failed.Request);
            Assert.Null(failed.Draft);
            Assert.Empty(failed.Messages.Where(message => message.Direction == MessageDirection.Outgoing));

            faulting.FailReschedule = false;
            if (staleOwnership)
            {
                await schedule.RefreshLinkedContextAsync();
                await schedule.AssignRequestToMeCommand.ExecuteAsync(null);
            }
            await schedule.AcceptSuggestedUpdateCommand.ExecuteAsync(null);
            Assert.Null(schedule.ErrorText);
            var retried = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(before.Appointment!.Revision + 1, retried.Appointment!.Revision);
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(context.Clock.ToLocal(retried.Appointment.StartUtc)));
            Assert.Equal(SchedulingWorkStatus.ReplyPending, retried.Request.Status);
            Assert.False(schedule.CanSaveChanges);
        });
    }

    [Fact]
    public async Task Regenerating_reply_preserves_staff_edited_draft_and_records_it_without_rescheduling()
    {
        await WithWorkflow(async context =>
        {
            var details = await ClaimMadsAsync(context.Workflow);
            var composer = new ReplyComposerViewModel(context.Workflow, context.TimeProvider, NullLogger<ReplyComposerViewModel>.Instance);
            var replyService = new CapturingReplySuggestionService();
            var schedule = new SchedulingViewModel(context.Scheduling, context.Store, [new SampleDraftService(), new SampleDraftService()], context.Clock, context.TimeProvider, NullLogger<SchedulingViewModel>.Instance, context.Workflow, composer, [replyService, replyService]);
            await schedule.InitializeAsync(default);
            Assert.True(await schedule.OpenAppointmentAsync(details.Appointment!.Id, details.Request.Id));
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);
            await schedule.EditSuggestedReplyCommand.ExecuteAsync(null);
            var draftId = composer.Draft!.Id;
            composer.Text = "Would Wednesday afternoon work for you?";
            Assert.True(await composer.FlushAsync());
            schedule.ProposedTime = "11:00 AM";
            await schedule.PrepareReplySuggestionCommand.ExecuteAsync(null);

            Assert.Contains("11:00 AM", schedule.SuggestedReplyText);
            Assert.Equal(draftId, composer.Draft!.Id);
            Assert.Equal("Would Wednesday afternoon work for you?", composer.Text);
            var pending = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            Assert.Equal(composer.Text, pending.Draft!.Body);
            Assert.True(pending.Draft.HasStaffEdits);
            Assert.Null(pending.Draft.AppointmentRevision);
            Assert.Equal(details.Appointment, pending.Appointment);
            Assert.Empty(pending.Messages.Where(message => message.Direction == MessageDirection.Outgoing));

            await schedule.RecordReplyDraftCommand.ExecuteAsync(null);
            var recorded = (await context.Workflow.GetConversationAsync(details.Conversation.Id))!;
            var outgoing = Assert.Single(recorded.Messages, message => message.Direction == MessageDirection.Outgoing);
            Assert.Equal("Would Wednesday afternoon work for you?", outgoing.Body);
            Assert.Equal(draftId, outgoing.SourceDraftId);
            Assert.Equal("Recorded locally · not delivered", outgoing.RecordStatus);
            Assert.Equal(SchedulingWorkStatus.WaitingOnPatient, recorded.Request.Status);
            Assert.Null(recorded.Request.OutcomeAppointmentRevision);
            Assert.Equal(details.Appointment, recorded.Appointment);
            Assert.Null(recorded.Draft);
        });
    }

    private static async Task<ConversationDetails> ClaimMadsAsync(MessagingWorkflowService workflow)
    {
        var details = (await workflow.GetConversationAsync(MessagingSeedData.MadsConversationId))!;
        if (details.Request.OwnerId is null)
        {
            var claim = await workflow.AssignToMeAsync(details.Request);
            Assert.True(claim.Succeeded, claim.Error);
            details = (await workflow.GetConversationAsync(details.Conversation.Id))!;
        }
        return details;
    }

    private static MessagingWorkflowService CreateWorkflow(IWorkflowStore store, SchedulingService scheduling, ClinicClock clock, TimeProvider timeProvider, StaffMember current) =>
        new(store, scheduling, clock, timeProvider, new StaffContext(current, MessagingSeedData.Staff));

    private static async Task WithWorkflow(Func<TestContext, Task> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "CareDesk.Workflow.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "test.db");
        var clock = new ClinicClock(ClinicZone);
        var timeProvider = new FixedTimeProvider(Now);
        var store = new SqliteAppointmentStore(path, clock, timeProvider);
        try
        {
            await store.InitializeAsync(default);
            var scheduling = new SchedulingService(store, clock, timeProvider);
            var workflow = CreateWorkflow(store, scheduling, clock, timeProvider, MessagingSeedData.CurrentStaff);
            await action(new(store, scheduling, workflow, clock, timeProvider, path));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed record TestContext(SqliteAppointmentStore Store, SchedulingService Scheduling, MessagingWorkflowService Workflow, ClinicClock Clock, TimeProvider TimeProvider, string Path);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FaultingWorkflowStore(IWorkflowStore inner) : IWorkflowStore
    {
        public bool FailDraftSave { get; set; }
        public bool FailReplyRecord { get; set; }
        public bool FailReschedule { get; set; }
        public Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(StaffId currentStaffId, CancellationToken cancellationToken) => inner.GetConversationSummariesAsync(currentStaffId, cancellationToken);
        public Task<ConversationDetails?> GetConversationAsync(ConversationId conversationId, StaffId currentStaffId, CancellationToken cancellationToken) => inner.GetConversationAsync(conversationId, currentStaffId, cancellationToken);
        public Task<ConversationDetails?> GetRequestForAppointmentAsync(AppointmentId appointmentId, RequestId? preferredRequestId, StaffId currentStaffId, CancellationToken cancellationToken) => inner.GetRequestForAppointmentAsync(appointmentId, preferredRequestId, currentStaffId, cancellationToken);
        public Task MarkConversationReadAsync(ConversationId conversationId, StaffId staffId, DateTimeOffset readUtc, CancellationToken cancellationToken) => inner.MarkConversationReadAsync(conversationId, staffId, readUtc, cancellationToken);
        public Task<SchedulingRequest> UpdateOwnerAsync(RequestId requestId, StaffId? ownerId, SchedulingWorkStatus status, int expectedVersion, CancellationToken cancellationToken) => inner.UpdateOwnerAsync(requestId, ownerId, status, expectedVersion, cancellationToken);
        public Task<ReplyDraft> SaveDraftAsync(ReplyDraft draft, int expectedRequestVersion, int? expectedDraftRevision, CancellationToken cancellationToken) => FailDraftSave ? Task.FromException<ReplyDraft>(new IOException("Draft storage unavailable")) : inner.SaveDraftAsync(draft, expectedRequestVersion, expectedDraftRevision, cancellationToken);
        public Task<RequestMutation> CommitRescheduleAsync(RequestId requestId, int expectedRequestVersion, Appointment appointment, CancellationToken cancellationToken) => FailReschedule ? Task.FromException<RequestMutation>(new IOException("Appointment storage unavailable")) : inner.CommitRescheduleAsync(requestId, expectedRequestVersion, appointment, cancellationToken);
        public Task<RequestMutation> RecordReplyAsync(RequestId requestId, int expectedRequestVersion, ReplyDraft draft, CommunicationMessage message, SchedulingWorkStatus finalStatus, CancellationToken cancellationToken) => FailReplyRecord ? Task.FromException<RequestMutation>(new IOException("Reply storage unavailable")) : inner.RecordReplyAsync(requestId, expectedRequestVersion, draft, message, finalStatus, cancellationToken);
    }

    private sealed class DelayedWorkflowStore(IWorkflowStore inner) : IWorkflowStore
    {
        public ConversationId? DelayConversationId { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(StaffId currentStaffId, CancellationToken cancellationToken) => inner.GetConversationSummariesAsync(currentStaffId, cancellationToken);
        public async Task<ConversationDetails?> GetConversationAsync(ConversationId conversationId, StaffId currentStaffId, CancellationToken cancellationToken)
        {
            if (conversationId == DelayConversationId)
            {
                Started.TrySetResult();
                await Release.Task;
            }
            return await inner.GetConversationAsync(conversationId, currentStaffId, cancellationToken);
        }
        public Task<ConversationDetails?> GetRequestForAppointmentAsync(AppointmentId appointmentId, RequestId? preferredRequestId, StaffId currentStaffId, CancellationToken cancellationToken) => inner.GetRequestForAppointmentAsync(appointmentId, preferredRequestId, currentStaffId, cancellationToken);
        public Task MarkConversationReadAsync(ConversationId conversationId, StaffId staffId, DateTimeOffset readUtc, CancellationToken cancellationToken) => inner.MarkConversationReadAsync(conversationId, staffId, readUtc, cancellationToken);
        public Task<SchedulingRequest> UpdateOwnerAsync(RequestId requestId, StaffId? ownerId, SchedulingWorkStatus status, int expectedVersion, CancellationToken cancellationToken) => inner.UpdateOwnerAsync(requestId, ownerId, status, expectedVersion, cancellationToken);
        public Task<ReplyDraft> SaveDraftAsync(ReplyDraft draft, int expectedRequestVersion, int? expectedDraftRevision, CancellationToken cancellationToken) => inner.SaveDraftAsync(draft, expectedRequestVersion, expectedDraftRevision, cancellationToken);
        public Task<RequestMutation> CommitRescheduleAsync(RequestId requestId, int expectedRequestVersion, Appointment appointment, CancellationToken cancellationToken) => inner.CommitRescheduleAsync(requestId, expectedRequestVersion, appointment, cancellationToken);
        public Task<RequestMutation> RecordReplyAsync(RequestId requestId, int expectedRequestVersion, ReplyDraft draft, CommunicationMessage message, SchedulingWorkStatus finalStatus, CancellationToken cancellationToken) => inner.RecordReplyAsync(requestId, expectedRequestVersion, draft, message, finalStatus, cancellationToken);
    }

    private sealed class CapturingReplySuggestionService : IReplySuggestionService
    {
        public string ModeLabel => "Test mode";
        public List<ReplySuggestionRequest> Requests { get; } = [];

        public Task<ReplySuggestionResult> PrepareAsync(ReplySuggestionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var state = request.IsCommitted ? "saved" : "proposed";
            return Task.FromResult(ReplySuggestionResult.Success($"The {state} time is {request.ProposedLocalStart:h:mm tt} with {request.ClinicianName}.", "Test AI reply"));
        }
    }

    private sealed class DelayedReplySuggestionService : IReplySuggestionService
    {
        public string ModeLabel => "Test mode";
        public bool DelayNext { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReplySuggestionResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReplySuggestionResult> PrepareAsync(ReplySuggestionRequest request, CancellationToken cancellationToken)
        {
            if (DelayNext)
            {
                DelayNext = false;
                Started.TrySetResult();
                return await Release.Task;
            }
            return ReplySuggestionResult.Success($"Current suggestion for {request.ProposedLocalStart:h:mm tt} with {request.ClinicianName}.", "Test AI reply");
        }
    }
}
