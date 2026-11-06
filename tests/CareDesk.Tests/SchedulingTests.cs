using System.IO;
using CareDesk.Core;
using CareDesk.Desktop;
using CareDesk.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CareDesk.Tests;

public sealed class SchedulingTests
{
    private static readonly TimeZoneInfo ClinicZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reschedule_rejects_past_time_and_preserves_saved_appointment()
    {
        await WithStore(async (store, service, clock) =>
        {
            var current = (await store.GetAppointmentsAsync(default)).OrderBy(a => a.StartUtc).First();
            var past = clock.ToLocal(current.StartUtc).AddDays(-1);
            var result = await service.RescheduleAsync(new(current.Id, current.ClinicianId, past, 30));
            Assert.False(result.Succeeded);
            Assert.Contains("future", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(current, (await store.GetAppointmentsAsync(default)).Single(a => a.Id == current.Id));
        });
    }

    [Fact]
    public async Task Reschedule_requires_known_appointment_clinician_and_positive_interval()
    {
        await WithStore(async (store, service, clock) =>
        {
            var current = (await store.GetAppointmentsAsync(default)).OrderBy(a => a.StartUtc).First();
            var local = clock.ToLocal(current.StartUtc).AddHours(2);
            var missing = await service.RescheduleAsync(new(new(Guid.NewGuid()), current.ClinicianId, local, 30));
            Assert.False(missing.Succeeded);
            Assert.Contains("no longer available", missing.Error, StringComparison.OrdinalIgnoreCase);
            var unknownClinician = await service.RescheduleAsync(new(current.Id, new(Guid.NewGuid()), local, 30));
            Assert.False(unknownClinician.Succeeded);
            Assert.Contains("clinician", unknownClinician.Error, StringComparison.OrdinalIgnoreCase);
            var invalidDuration = await service.RescheduleAsync(new(current.Id, current.ClinicianId, local, 0));
            Assert.False(invalidDuration.Succeeded);
            Assert.Contains("Duration", invalidDuration.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Sample_draft_is_typed_supported_and_does_not_save()
    {
        await WithStore(async (store, service, clock) =>
        {
            var current = (await store.GetAppointmentsAsync(default)).OrderBy(a => a.StartUtc).First();
            var clinicians = await store.GetCliniciansAsync(default);
            var adapter = new SampleDraftService();
            var request = new DraftRequest(current, "Could you move this appointment to 10:00 AM on the same day?", DateOnly.FromDateTime(clock.ToLocal(current.StartUtc)), clock.TimeZone.Id, clinicians);
            var draft = await adapter.PrepareAsync(request, default);
            Assert.True(draft.Succeeded);
            Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(clock.ToLocal(current.StartUtc)));
            Assert.Equal(new TimeOnly(10, 0), TimeOnly.FromDateTime(draft.Draft!.LocalStart!.Value));
            Assert.NotEqual(clock.ToLocal(current.StartUtc), draft.Draft.LocalStart.Value);
            Assert.Equal("Sample recommendation - no AI call", draft.Draft.Source);
            Assert.Equal(current, (await store.GetAppointmentsAsync(default)).Single(a => a.Id == current.Id));
            var unsupported = await adapter.PrepareAsync(request with { Message = "Please move this to Friday at noon." }, default);
            Assert.False(unsupported.Succeeded);
            Assert.Contains("supports the example", unsupported.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Saved_reschedule_survives_reopen_and_reset_restores_future_seed()
    {
        await WithStore(async (store, service, clock, path) =>
        {
            var current = (await store.GetAppointmentsAsync(default)).OrderBy(a => a.StartUtc).First();
            var clinicDay = DateOnly.FromDateTime(clock.ToLocal(current.StartUtc));
            var newStart = clinicDay.ToDateTime(new TimeOnly(11, 0));
            var result = await service.RescheduleAsync(new(current.Id, current.ClinicianId, newStart, 30));
            Assert.True(result.Succeeded, result.Error);
            var reopened = new SqliteAppointmentStore(path, clock, new FixedTimeProvider(Now));
            await reopened.InitializeAsync(default);
            var saved = (await reopened.GetAppointmentsAsync(default)).Single(a => a.Id == current.Id);
            Assert.Equal(newStart, clock.ToLocal(saved.StartUtc));
            Assert.Equal(current.PatientName, saved.PatientName);
            Assert.Equal(SeedData.ReschedulingMessage, current.PatientRequest);
            Assert.Null(saved.PatientRequest);
            await service.ResetAsync();
            var reset = (await store.GetAppointmentsAsync(default)).Single(a => a.Id == current.Id);
            Assert.Equal(SeedData.ReschedulingMessage, reset.PatientRequest);
            Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(clock.ToLocal(reset.StartUtc)));
            Assert.True(clock.ToLocal(reset.StartUtc) > Now.LocalDateTime);
        });
    }

    [Fact]
    public async Task Storage_failure_is_not_converted_to_success()
    {
        var clock = new ClinicClock(ClinicZone);
        var store = new FailingAppointmentStore();
        var service = new SchedulingService(store, clock, new FixedTimeProvider(Now));
        var appointmentId = new AppointmentId(Guid.Parse("5c54fa11-3fce-4d43-b79a-5988c620e901"));
        await Assert.ThrowsAsync<IOException>(() => service.RescheduleAsync(new(appointmentId, SeedData.Clinicians[0].Id, new DateTime(2026, 1, 5, 10, 0, 0), 30)));
    }

    [Fact]
    public async Task Patient_request_is_shown_only_for_its_appointment_and_suggestion_requires_review_and_save()
    {
        await WithStore(async (store, service, clock) =>
        {
            var vm = CreateViewModel(store, service, clock, new SampleDraftService());
            await vm.InitializeAsync(default);
            await vm.PrepareDraftCommand.ExecutionTask!;
            var requested = vm.SelectedRow;
            var originalTime = vm.ProposedTime;
            Assert.Equal(SeedData.ReschedulingMessage, vm.PatientRequest);
            Assert.True(vm.HasPatientRequest);
            Assert.True(requested!.HasPatientRequest);
            Assert.Contains("needs attention", requested.ToString());
            Assert.Contains("10:00", vm.SuggestedAction);
            Assert.Contains("Sample", vm.DraftStatus);
            Assert.False(vm.IsDirty);
            Assert.True(vm.CanApplySuggestion);
            vm.SelectedRow = vm.Appointments.Single(a => a.Patient == "Anson Horton");
            Assert.False(vm.HasPatientRequest);
            Assert.Empty(vm.PatientRequest);
            Assert.Empty(vm.SuggestedAction);
            Assert.False(vm.CanApplySuggestion);
            vm.SelectedRow = requested;
            await vm.PrepareDraftCommand.ExecutionTask!;
            Assert.Equal(originalTime, vm.ProposedTime);
            vm.ProposedClinician = vm.Clinicians.Single(c => c.DisplayName == "Dr. Jeff Kelly");
            vm.ProposedDuration = 60;
            vm.ApplySuggestionCommand.Execute(null);
            Assert.Equal(requested!.Appointment.ClinicianId, vm.ProposedClinician!.Id);
            Assert.Equal(30, vm.ProposedDuration);
            Assert.True(vm.IsDirty);
            Assert.Equal(SeedData.ReschedulingMessage, vm.PatientRequest);
            Assert.Equal(SeedData.ReschedulingMessage, (await store.GetAppointmentsAsync(default)).Single(a => a.Id == requested!.Appointment.Id).PatientRequest);
            await vm.ConfirmRescheduleCommand.ExecuteAsync(null);
            Assert.False(vm.HasPatientRequest);
            Assert.False(vm.IsDirty);
            Assert.False(vm.Appointments.Single(a => a.Appointment.Id == requested!.Appointment.Id).HasPatientRequest);
            Assert.DoesNotContain("needs attention", vm.SelectedRow!.ToString());
            Assert.Null((await store.GetAppointmentsAsync(default)).Single(a => a.Id == requested!.Appointment.Id).PatientRequest);
            await vm.ResetAsync();
            await vm.PrepareDraftCommand.ExecutionTask!;
            Assert.True(vm.HasPatientRequest);
            Assert.Equal(SeedData.ReschedulingMessage, vm.PatientRequest);
            Assert.False(vm.IsDirty);
            Assert.True(vm.CanApplySuggestion);
        });
    }

    [Fact]
    public async Task Recommendation_refresh_preserves_manual_edits_and_failed_save_preserves_request()
    {
        await WithStore(async (store, service, clock) =>
        {
            var vm = CreateViewModel(store, service, clock, new SampleDraftService());
            await vm.InitializeAsync(default);
            await vm.PrepareDraftCommand.ExecutionTask!;
            vm.ProposedTime = "11:00 AM";
            await vm.PrepareDraftCommand.ExecuteAsync(null);
            Assert.Equal("11:00 AM", vm.ProposedTime);
            vm.ProposedDate = new DateTime(2025, 12, 31);
            await vm.ConfirmRescheduleCommand.ExecuteAsync(null);
            Assert.Contains("future", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.True(vm.HasPatientRequest);
            Assert.Equal("11:00 AM", vm.ProposedTime);
            Assert.Equal(new DateTime(2025, 12, 31), vm.ProposedDate);
            Assert.Equal(SeedData.ReschedulingMessage, (await store.GetAppointmentsAsync(default)).Single(a => a.Id == vm.SelectedRow!.Appointment.Id).PatientRequest);
        });
    }

    [Fact]
    public async Task Late_recommendation_cannot_leak_to_another_appointment()
    {
        await WithStore(async (store, service, clock) =>
        {
            var pending = new TaskCompletionSource<DraftResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = CreateViewModel(store, service, clock, new DeferredDraftService(pending));
            await vm.InitializeAsync(default);
            var running = vm.PrepareDraftCommand.ExecutionTask!;
            Assert.True(vm.IsPreparingSuggestion);
            vm.SelectedRow = vm.Appointments.Single(a => a.Patient == "Anson Horton");
            pending.SetResult(DraftResult.Success(new(new DateTime(2026, 1, 2, 10, 0, 0), null, null, "Sample draft", null)));
            await running;
            Assert.False(vm.HasPatientRequest);
            Assert.Empty(vm.SuggestedAction);
            Assert.False(vm.CanApplySuggestion);
            Assert.False(vm.IsDirty);
            Assert.False(vm.IsPreparingSuggestion);
        });
    }

    [Fact]
    public async Task Unavailable_live_recommendation_does_not_fall_back_or_change_booking()
    {
        await WithStore(async (store, service, clock) =>
        {
            var live = new OpenAiDraftService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), clock, NullLogger<OpenAiDraftService>.Instance);
            var vm = new SchedulingViewModel(service, store, [new SampleDraftService(), live], clock, new FixedTimeProvider(Now), NullLogger<SchedulingViewModel>.Instance);
            await vm.InitializeAsync(default);
            await vm.PrepareDraftCommand.ExecutionTask!;
            vm.Mode = "Live mode";
            await vm.PrepareDraftCommand.ExecutionTask!;
            Assert.Contains("not configured", vm.SuggestedAction);
            Assert.Contains("Live", vm.DraftStatus);
            Assert.False(vm.CanApplySuggestion);
            Assert.False(vm.IsDirty);
            Assert.Equal(SeedData.ReschedulingMessage, vm.PatientRequest);
        });
    }

    [Fact]
    public async Task Legacy_database_migration_preserves_bookings_and_does_not_recreate_completed_requests()
    {
        await WithStore(async (store, service, clock, path) =>
        {
            var current = (await store.GetAppointmentsAsync(default)).OrderBy(a => a.StartUtc).First();
            var moved = current with { StartUtc = current.StartUtc.AddHours(2), EndUtc = current.EndUtc.AddHours(2) };
            await store.SaveAsync(moved, default);
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE appointments DROP COLUMN patient_request; PRAGMA user_version=0;";
                await command.ExecuteNonQueryAsync();
            }
            await store.InitializeAsync(default);
            var migrated = (await store.GetAppointmentsAsync(default)).Single(a => a.Id == current.Id);
            Assert.Equal(moved.StartUtc, migrated.StartUtc);
            Assert.Equal(SeedData.ReschedulingMessage, migrated.PatientRequest);
            Assert.Single(await store.GetAppointmentsAsync(default), a => a.PatientRequest is not null);
            var result = await service.RescheduleAsync(new(migrated.Id, migrated.ClinicianId, clock.ToLocal(migrated.StartUtc).AddHours(1), 30));
            Assert.True(result.Succeeded, result.Error);
            await store.InitializeAsync(default);
            Assert.Null((await store.GetAppointmentsAsync(default)).Single(a => a.Id == current.Id).PatientRequest);
        });
    }

    [Fact]
    public async Task Agenda_refresh_updates_request_indicators_and_details_from_saved_data()
    {
        await WithStore(async (store, service, clock) =>
        {
            var vm = CreateViewModel(store, service, clock, new SampleDraftService());
            await vm.InitializeAsync(default);
            await vm.PrepareDraftCommand.ExecutionTask!;
            var appointment = vm.SelectedRow!.Appointment;
            await store.SaveAsync(appointment with { PatientRequest = null }, default);
            await vm.RefreshAgendaCommand.ExecuteAsync(null);
            Assert.Equal(appointment.Id, vm.SelectedRow!.Appointment.Id);
            Assert.False(vm.SelectedRow.HasPatientRequest);
            Assert.False(vm.HasPatientRequest);
            Assert.Empty(vm.SuggestedAction);
            await store.SaveAsync(appointment, default);
            await vm.RefreshAgendaCommand.ExecuteAsync(null);
            await vm.PrepareDraftCommand.ExecutionTask!;
            Assert.True(vm.SelectedRow!.HasPatientRequest);
            Assert.True(vm.HasPatientRequest);
            Assert.Equal(SeedData.ReschedulingMessage, vm.PatientRequest);
            vm.ProposedTime = "11:00 AM";
            await vm.RefreshAgendaCommand.ExecuteAsync(null);
            Assert.Equal("11:00 AM", vm.ProposedTime);
            Assert.True(vm.IsDirty);
            Assert.Contains("Save your changes", vm.ErrorText);
        });
    }

    private static SchedulingViewModel CreateViewModel(IAppointmentStore store, SchedulingService service, ClinicClock clock, ISchedulingDraftService draft) =>
        new(service, store, [draft, draft], clock, new FixedTimeProvider(Now), NullLogger<SchedulingViewModel>.Instance);

    private sealed class DeferredDraftService(TaskCompletionSource<DraftResult> pending) : ISchedulingDraftService
    {
        public string ModeLabel => "Sample mode - no AI call";
        public Task<DraftResult> PrepareAsync(DraftRequest request, CancellationToken cancellationToken) => pending.Task;
    }

    private static async Task WithStore(Func<SqliteAppointmentStore, SchedulingService, ClinicClock, Task> action) =>
        await WithStore((store, service, clock, _) => action(store, service, clock));

    private static async Task WithStore(Func<SqliteAppointmentStore, SchedulingService, ClinicClock, string, Task> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "CareDesk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "test.db");
        var clock = new ClinicClock(ClinicZone);
        var store = new SqliteAppointmentStore(path, clock, new FixedTimeProvider(Now));
        try
        {
            await store.InitializeAsync(default);
            var service = new SchedulingService(store, clock, new FixedTimeProvider(Now));
            await action(store, service, clock, path);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingAppointmentStore : IAppointmentStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Clinician>> GetCliniciansAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Clinician>>(SeedData.Clinicians);
        public Task<IReadOnlyList<Appointment>> GetAppointmentsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Appointment>>([new(new(Guid.Parse("5c54fa11-3fce-4d43-b79a-5988c620e901")), "Rachel Kang", "CD-1048", SeedData.Clinicians[0].Id, SeedData.Clinicians[0].DisplayName, "Follow-up", DateTimeOffset.Parse("2026-01-05T14:00:00Z"), DateTimeOffset.Parse("2026-01-05T14:30:00Z"), "Scheduled")]);
        public Task SaveAsync(Appointment appointment, CancellationToken cancellationToken) => Task.FromException(new IOException("Storage unavailable"));
        public Task ResetAsync(IReadOnlyList<Clinician> clinicians, IReadOnlyList<Appointment> appointments, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
