using DoodhDirect.Domain.Setup;

namespace DoodhDirect.Domain.Tests;

/// <summary>
/// Domain-level coverage for the scoped numbering series (requirement 14): a series
/// code may carry one independent counter per scope, with an empty scope denoting the
/// legacy global series.
/// </summary>
public sealed class NumberSeriesDomainTests
{
    private static readonly DateTime IndiaLocal =
        new(2026, 8, 28, 12, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void Constructor_NormalizesEmptyScopeToGlobal()
    {
        var global = new NumberSeries(
            "ORDER", "Order Number", "ORD/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never);

        Assert.Equal("ORDER", global.Code);
        Assert.Equal(string.Empty, global.ScopeKey);
        Assert.True(global.IsActive);
        Assert.Equal(0, global.LastUsedNumber); // startingNumber - 1
    }

    [Fact]
    public void Constructor_NormalizesAndUppercasesScope()
    {
        var scoped = new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, " main ");

        Assert.Equal("MAIN", scoped.ScopeKey);
    }

    [Fact]
    public void Constructor_RejectsInvalidIncrement()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NumberSeries(
            "ORDER", "Order Number", "ORD/{NUMBER:000000}", 1, 0, NumberSeriesResetPolicy.Never));
    }

    [Fact]
    public void SameCode_DifferentScopes_KeepIndependentCounters()
    {
        var main = new NumberSeries(
            "ORDER", "Main", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN");
        var nit = new NumberSeries(
            "ORDER", "NIT", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NIT");

        Assert.Equal("MAIN", main.ScopeKey);
        Assert.Equal("NIT", nit.ScopeKey);
        Assert.NotEqual(main.ScopeKey, nit.ScopeKey);

        Assert.Equal(1, main.NextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal), IndiaLocal));
        Assert.Equal(1, nit.NextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal), IndiaLocal));
        Assert.Equal(2, main.NextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal), IndiaLocal));

        // Each scope advanced independently.
        Assert.Equal(2, main.LastUsedNumber);
        Assert.Equal(1, nit.LastUsedNumber);
    }

    [Fact]
    public void GlobalAndScopedSeries_AreDistinctRows()
    {
        var global = new NumberSeries(
            "ORDER", "Legacy", "ORD/{NUMBER:000000}", 100, 1, NumberSeriesResetPolicy.Never);
        var scoped = new NumberSeries(
            "ORDER", "Main", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN");

        Assert.Equal(string.Empty, global.ScopeKey);
        Assert.Equal("MAIN", scoped.ScopeKey);

        Assert.Equal(100, global.NextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal), IndiaLocal));
        Assert.Equal(1, scoped.NextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal), IndiaLocal));
    }

    [Fact]
    public void ScopeKey_IsImmutableAfterConstruction()
    {
        var series = new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN");

        Assert.Equal("MAIN", series.ScopeKey);
    }

    [Fact]
    public void PeekNextNumber_DoesNotConsumeScopedCounter()
    {
        var series = new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN");

        var peeked = series.PeekNextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal));

        Assert.Equal(1, peeked);
        Assert.Equal(0, series.LastUsedNumber);
    }

    [Fact]
    public void DeactivatedSeries_KeepsScopeAndCounter()
    {
        var series = new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN");
        series.NextNumber(NumberSeriesResetPolicy.Never, DateOnly.FromDateTime(IndiaLocal), IndiaLocal);

        series.Deactivate(7, IndiaLocal);

        Assert.False(series.IsActive);
        Assert.Equal("MAIN", series.ScopeKey);
        Assert.Equal(1, series.LastUsedNumber);
    }

    [Fact]
    public void FinancialYear_HandlesScopeIndependentPeriods()
    {
        Assert.Equal("MAIN", new NumberSeries(
            "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.FinancialYear, "MAIN").ScopeKey);

        Assert.Equal((2025, 2026), NumberSeries.FinancialYear(new DateOnly(2026, 3, 31)));
        Assert.Equal((2026, 2027), NumberSeries.FinancialYear(new DateOnly(2026, 4, 1)));
        Assert.Equal("26-27", NumberSeries.FormatFinancialYear(new DateOnly(2026, 8, 28)));
    }
}
