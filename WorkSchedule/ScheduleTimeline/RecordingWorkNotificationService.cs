// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces;

namespace Klacks.IntegrationTest.WorkSchedule.ScheduleTimeline;

/// <summary>
/// Work notification recorder for the schedule timeline host tests: keeps the collision and validation pushes and
/// signals each validation push, every other notification is accepted and dropped.
/// </summary>
public sealed class RecordingWorkNotificationService : IWorkNotificationService
{
    private readonly SemaphoreSlim _validationSignal = new(0);

    public ConcurrentQueue<CollisionListNotificationDto> Collisions { get; } = new();

    public ConcurrentQueue<ScheduleValidationListNotificationDto> Validations { get; } = new();

    public async Task<bool> WaitForValidationsAsync(int count, TimeSpan timeout)
    {
        for (var i = 0; i < count; i++)
        {
            if (!await _validationSignal.WaitAsync(timeout))
            {
                return false;
            }
        }

        return true;
    }

    public Task NotifyCollisionsDetected(CollisionListNotificationDto notification)
    {
        Collisions.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task NotifyScheduleValidationsDetected(ScheduleValidationListNotificationDto notification)
    {
        Validations.Enqueue(notification);
        _validationSignal.Release();
        return Task.CompletedTask;
    }

    public Task NotifyWorkCreated(WorkNotificationDto notification) => Task.CompletedTask;

    public Task NotifyWorksBulkCreated(WorksBulkCreatedNotificationDto notification) => Task.CompletedTask;

    public Task NotifyWizard4CandidatesChanged(Wizard4CandidateNotificationDto notification) => Task.CompletedTask;

    public Task NotifyWorkUpdated(WorkNotificationDto notification) => Task.CompletedTask;

    public Task NotifyWorkDeleted(WorkNotificationDto notification) => Task.CompletedTask;

    public Task NotifyScheduleUpdated(ScheduleNotificationDto notification) => Task.CompletedTask;

    public Task NotifyPeriodHoursUpdated(PeriodHoursNotificationDto notification) => Task.CompletedTask;

    public Task NotifyPeriodHoursRecalculated(DateOnly startDate, DateOnly endDate, Guid? analyseToken) => Task.CompletedTask;

    public Task NotifyThoroughRecalculationCompleted(ThoroughRecalculationCompletedDto notification) => Task.CompletedTask;

    public Task NotifyScheduleChangeTracked(ScheduleChangeNotificationDto notification) => Task.CompletedTask;
}
