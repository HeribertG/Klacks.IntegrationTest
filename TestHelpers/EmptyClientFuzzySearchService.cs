using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.IntegrationTest.TestHelpers;

public sealed class EmptyClientFuzzySearchService : IClientFuzzySearchService
{
    public Task<List<Client>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<Client>());
    }
}
