using BarcodePrinter.Contracts;
using BarcodePrinter.Domain;

namespace BarcodePrinter.Application.Printing;

/// <summary>What the strategy needs to decide a scope and a range.</summary>
public sealed record CartonNumberingContext(
    long ProductId,
    string ProductCode,
    string? Batch,
    DateOnly RunDate,
    int LabelCount,
    long? RequestedFrom,
    long? RequestedTo);

public sealed record CartonAllocation(long From, long To, long Total)
{
    public IEnumerable<long> Numbers => Enumerable.Range(0, (int)(To - From + 1)).Select(i => From + i);
}

/// <summary>Allocates the next block for a scope, atomically. Implemented in
/// Infrastructure over `carton_sequences` with SELECT … FOR UPDATE.</summary>
public interface ICartonSequenceAllocator
{
    Task<long> ReserveAsync(string scopeKey, string strategyCode, int count,
        System.Data.Common.DbTransaction tx, CancellationToken ct);
}

/// <summary>
/// C-11 is unresolved (user-entered vs allocated, reset scope, gapless).
/// Every candidate lives behind this interface and is selected by the
/// `Printing:CartonStrategy` setting, so resolving C-11 is configuration or a
/// single new class — never a change to the print pipeline.
/// </summary>
public interface ICartonNumberingStrategy
{
    string Code { get; }

    /// <summary>True when the operator supplies the range (legacy CTN Start/End).</summary>
    bool RequiresManualRange { get; }

    string BuildScopeKey(CartonNumberingContext context);

    Task<CartonAllocation> AllocateAsync(CartonNumberingContext context,
        System.Data.Common.DbTransaction tx, CancellationToken ct);

    /// <summary>Rendered carton text. C-10 (bare number vs "n of N") is a
    /// formatting decision here, never in the renderer.</summary>
    string Format(long cartonNo, CartonAllocation allocation);
}

/// <summary>Shared validation so every strategy rejects the same nonsense.</summary>
public abstract class CartonNumberingStrategyBase(CartonNumberFormat format) : ICartonNumberingStrategy
{
    public abstract string Code { get; }
    public abstract bool RequiresManualRange { get; }
    public abstract string BuildScopeKey(CartonNumberingContext context);
    public abstract Task<CartonAllocation> AllocateAsync(
        CartonNumberingContext context, System.Data.Common.DbTransaction tx, CancellationToken ct);

    public virtual string Format(long cartonNo, CartonAllocation allocation) => format switch
    {
        CartonNumberFormat.OfTotal => $"{cartonNo} of {allocation.Total}",
        CartonNumberFormat.Padded3 => cartonNo.ToString("000"),
        _ => cartonNo.ToString(),
    };

    protected static void ValidateCount(int labelCount)
    {
        if (labelCount is < 1 or > 10_000)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Number of labels must be between 1 and 10,000.");
        }
    }
}

/// <summary>C-10: how a carton number appears on the label.</summary>
public enum CartonNumberFormat { Bare, OfTotal, Padded3 }

/// <summary>
/// v1 default — mirrors the legacy application's CTN Start / CTN End inputs.
/// Chosen because it cannot silently produce numbers the client did not ask
/// for while C-11 is open.
/// </summary>
public sealed class ManualRangeCartonStrategy(CartonNumberFormat format)
    : CartonNumberingStrategyBase(format)
{
    public override string Code => "ManualRange";
    public override bool RequiresManualRange => true;
    public override string BuildScopeKey(CartonNumberingContext context) => string.Empty;

    public override Task<CartonAllocation> AllocateAsync(
        CartonNumberingContext context, System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        if (context.RequestedFrom is not { } from || context.RequestedTo is not { } to)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Enter the carton start and end numbers.");
        }
        if (from < 1)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "Carton start must be 1 or greater.");
        }
        if (to < from)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Carton end must not be less than carton start.");
        }

        var total = to - from + 1;
        ValidateCount((int)total);
        return Task.FromResult(new CartonAllocation(from, to, total));
    }
}

/// <summary>System-allocated, never resets.</summary>
public sealed class ContinuousPerProductStrategy(
    ICartonSequenceAllocator allocator, CartonNumberFormat format)
    : CartonNumberingStrategyBase(format)
{
    public override string Code => "ContinuousPerProduct";
    public override bool RequiresManualRange => false;
    public override string BuildScopeKey(CartonNumberingContext context) => $"product:{context.ProductId}";

    public override async Task<CartonAllocation> AllocateAsync(
        CartonNumberingContext context, System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        ValidateCount(context.LabelCount);
        var last = await allocator.ReserveAsync(
            BuildScopeKey(context), Code, context.LabelCount, tx, ct);
        var from = last - context.LabelCount + 1;
        return new CartonAllocation(from, last, context.LabelCount);
    }
}

/// <summary>System-allocated, resets per production batch.</summary>
public sealed class ContinuousPerProductBatchStrategy(
    ICartonSequenceAllocator allocator, CartonNumberFormat format)
    : CartonNumberingStrategyBase(format)
{
    public override string Code => "ContinuousPerProductBatch";
    public override bool RequiresManualRange => false;

    public override string BuildScopeKey(CartonNumberingContext context) =>
        $"product:{context.ProductId}|batch:{context.Batch ?? "-"}";

    public override async Task<CartonAllocation> AllocateAsync(
        CartonNumberingContext context, System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        ValidateCount(context.LabelCount);
        var last = await allocator.ReserveAsync(
            BuildScopeKey(context), Code, context.LabelCount, tx, ct);
        return new CartonAllocation(last - context.LabelCount + 1, last, context.LabelCount);
    }
}

/// <summary>System-allocated, resets daily per product.</summary>
public sealed class PerProductPerDayStrategy(
    ICartonSequenceAllocator allocator, CartonNumberFormat format)
    : CartonNumberingStrategyBase(format)
{
    public override string Code => "PerProductPerDay";
    public override bool RequiresManualRange => false;

    public override string BuildScopeKey(CartonNumberingContext context) =>
        $"product:{context.ProductId}|date:{context.RunDate:yyyyMMdd}";

    public override async Task<CartonAllocation> AllocateAsync(
        CartonNumberingContext context, System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        ValidateCount(context.LabelCount);
        var last = await allocator.ReserveAsync(
            BuildScopeKey(context), Code, context.LabelCount, tx, ct);
        return new CartonAllocation(last - context.LabelCount + 1, last, context.LabelCount);
    }
}

/// <summary>Resolves the configured strategy. Registered as a singleton; the
/// setting is read per call so an admin change takes effect on the next print.</summary>
public sealed class CartonStrategyResolver(
    ICartonSequenceAllocator allocator,
    Abstractions.ISettingsProvider settings)
{
    public async Task<ICartonNumberingStrategy> ResolveAsync(CancellationToken ct)
    {
        var code = await settings.GetAsync("Printing:CartonStrategy", ct) ?? "ManualRange";
        var format = Enum.TryParse<CartonNumberFormat>(
            await settings.GetAsync("Printing:CartonFormat", ct), ignoreCase: true, out var f)
            ? f : CartonNumberFormat.Bare;

        return code switch
        {
            "ContinuousPerProduct" => new ContinuousPerProductStrategy(allocator, format),
            "ContinuousPerProductBatch" => new ContinuousPerProductBatchStrategy(allocator, format),
            "PerProductPerDay" => new PerProductPerDayStrategy(allocator, format),
            _ => new ManualRangeCartonStrategy(format),
        };
    }
}
