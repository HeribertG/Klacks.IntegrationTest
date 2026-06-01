// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Drift-guard for the curated assistant ontology: every entity name in KlacksOntologyService must map
/// to a real EF entity CLR type. Generation from the DbContext was rejected (100+ entities would bloat
/// the prompt / blow the token budget); the curated subset is intentional, but it must not silently
/// drift from the schema. This runs in the integration project because building the EF model needs the
/// Npgsql provider (jsonb + HasDbFunction) — but it only reads Model metadata, no connection is opened.
/// Catches rename/removal of a curated entity; it does NOT detect "a newly relevant entity was not
/// added" — that addition stays a human judgement.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Ontology;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AI;

[TestFixture]
[Category("RealDatabase")]
public class OntologySchemaCorrespondenceTests
{
    [Test]
    public void EveryCuratedEntity_MapsToARealEfClrType()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var clrTypeNames = context.Model.GetEntityTypes()
            .Select(t => t.ClrType.Name)
            .ToHashSet();

        var ontology = new KlacksOntologyService();

        foreach (var entity in ontology.GetEntities())
        {
            clrTypeNames.ShouldContain(entity,
                $"Ontology entity '{entity}' has no matching EF entity type — the curated ontology has drifted from the schema (entity renamed/removed?).");
        }
    }
}
