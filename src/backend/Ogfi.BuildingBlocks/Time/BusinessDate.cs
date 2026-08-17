using System.Globalization;

namespace Ogfi.BuildingBlocks.Time;

public readonly record struct BusinessDate(DateOnly Value)
{
    public override string ToString() => Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
