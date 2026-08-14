using System.Data;
using Dapper;

namespace BarcodePrinter.Infrastructure.Persistence;

/// <summary>
/// Dapper has no built-in DateOnly support: MySQL DATE columns come back as
/// DateTime, and DateOnly cannot be used as a parameter at all. Registering a
/// handler once fixes reads AND writes everywhere, instead of every query
/// carrying its own conversion.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }
}

public sealed class TimeOnlyTypeHandler : SqlMapper.TypeHandler<TimeOnly>
{
    public override TimeOnly Parse(object value) => value switch
    {
        TimeOnly t => t,
        TimeSpan ts => TimeOnly.FromTimeSpan(ts),
        DateTime dt => TimeOnly.FromDateTime(dt),
        string s => TimeOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new DataException($"Cannot convert {value.GetType()} to TimeOnly."),
    };

    public override void SetValue(IDbDataParameter parameter, TimeOnly value)
    {
        parameter.DbType = DbType.Time;
        parameter.Value = value.ToTimeSpan();
    }
}

public static class DapperConfiguration
{
    private static bool _configured;
    private static readonly Lock Gate = new();

    public static void Configure()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            SqlMapper.AddTypeHandler(new TimeOnlyTypeHandler());
            _configured = true;
        }
    }
}
