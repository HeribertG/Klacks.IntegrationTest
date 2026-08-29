// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// I6 of the Klacksy-Autonomie test spec: a state hash over one container's weekday templates, used by
/// Az7 to prove create_container_template followed by its registered inverse (delete_container_template)
/// nets to no observable difference. Scoped to exactly what those two skills touch - the ContainerTemplate
/// rows of one container, ordered so insertion order never affects the hash - not the whole database.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Schedules;

namespace Klacks.IntegrationTest.TestHelpers;

public static class ContainerTemplateStateHasher
{
    public static async Task<string> ComputeAsync(IContainerTemplateRepository repository, Guid containerId)
    {
        var templates = await repository.GetTemplatesForContainer(containerId);

        var projection = templates
            .OrderBy(t => t.Weekday)
            .ThenBy(t => t.IsHoliday)
            .ThenBy(t => t.IsWeekdayAndHoliday)
            .Select(t => new
            {
                t.Weekday,
                t.IsHoliday,
                t.IsWeekdayAndHoliday,
                FromTime = t.FromTime.ToString("HH:mm"),
                UntilTime = t.UntilTime.ToString("HH:mm"),
                t.StartBase,
                t.EndBase,
                t.TransportMode,
                ItemCount = t.ContainerTemplateItems.Count
            })
            .ToList();

        var json = JsonSerializer.Serialize(projection);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}
