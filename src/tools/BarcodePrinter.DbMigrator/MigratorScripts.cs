using System.Reflection;

namespace BarcodePrinter.DbMigrator;

/// <summary>Lets integration tests run the exact production migration scripts
/// against a Testcontainers MySQL — the tests exercise the real schema, never
/// a lookalike.</summary>
public static class MigratorScripts
{
    public static Assembly Assembly => typeof(MigratorScripts).Assembly;
}
