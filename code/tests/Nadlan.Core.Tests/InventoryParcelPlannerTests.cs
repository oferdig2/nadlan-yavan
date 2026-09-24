using Nadlan.Import.Csv;
using Nadlan.Import.Legacy;
using Nadlan.Import.Parcels;

namespace Nadlan.Core.Tests;

public class InventoryParcelPlannerTests
{
    private const string Header = "Inventory Item,Agent-Property Identifier,Agent,Property Status,geographic area,OT,OT Ext.,Plot,Plot Ext.,KML coordinates,Plot SQM,Build. Factor,Inclination ~";
    private const string PolyA = "23.1,38.1 23.2,38.1 23.2,38.2 23.1,38.1";
    private const string PolyB = "23.3,38.3 23.4,38.3 23.4,38.4 23.3,38.3";

    private static ParcelImportPlan Plan(params string[] lines)
        => InventoryParcelPlanner.Plan(LegacyInventoryRow.ReadAll(CsvTable.Parse(Header + "\n" + string.Join("\n", lines))));

    [Fact]
    public void Two_agents_on_same_land_make_one_parcel()
    {
        var plan = Plan(
            $"OT1 Plot1,X-AgentA,AgentA,,Skroponeria,1,,1,,\"{PolyA}\",900,,",
            $"OT1 Plot1,X-AgentB,AgentB,,Skroponeria,1,,1,,\"{PolyA}\",900,,");

        var parcel = Assert.Single(plan.Parcels);
        Assert.Equal("TMP-SKR-OT1-P1", parcel.RegistryId);
        Assert.Single(parcel.LinkedRows);
        Assert.Empty(parcel.Warnings);
    }

    [Fact]
    public void Conflicting_geometry_keeps_majority_and_warns()
    {
        var plan = Plan(
            $"a,1,A,,Skroponeria,1,,1,,\"{PolyB}\",,,",
            $"b,2,B,,Skroponeria,1,,1,,\"{PolyA}\",,,",
            $"c,3,C,,Skroponeria,1,,1,,\"{PolyA}\",,,");

        var parcel = Assert.Single(plan.Parcels);
        Assert.Equal(2, parcel.SourceRow.RowNumber);
        Assert.Contains(parcel.Warnings, w => w.StartsWith("Geometry conflict: rows 1"));
    }

    [Fact]
    public void Greek_omicron_area_resolves_to_latin_name()
    {
        var plan = Plan($"a,1,A,,Οsmaes of Karystos,2,,8,,\"{PolyA}\",,,");

        Assert.Equal("OSK", plan.Parcels[0].Area.Code);
        Assert.Equal("Osmaes of Karystos", plan.Parcels[0].Area.Name);
    }

    [Fact]
    public void Implausible_inclination_is_dropped_with_warning()
    {
        var plan = Plan($"a,1,A,,Skroponeria,147,,4,,\"{PolyA}\",,,1535%");

        Assert.Null(plan.Parcels[0].Inclination);
        Assert.Contains(plan.Parcels[0].Warnings, w => w.Contains("1535%"));
    }

    [Fact]
    public void Rows_without_coordinates_are_counted_not_planned()
    {
        var plan = Plan("a,1,A,,Skroponeria,1,,1,,,,,");

        Assert.Empty(plan.Parcels);
        Assert.Empty(plan.Rejected);
        Assert.Equal(1, plan.RowsWithoutCoordinates);
    }
}
