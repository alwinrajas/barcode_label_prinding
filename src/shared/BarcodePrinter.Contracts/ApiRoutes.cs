namespace BarcodePrinter.Contracts;

/// <summary>Single source of truth for route strings — shared by the API's
/// endpoint mappings and the client's ApiClient so they cannot drift.</summary>
public static class ApiRoutes
{
    public const string Health = "/health";

    public static class Auth
    {
        public const string Login = "/api/auth/login";
        public const string Refresh = "/api/auth/refresh";
        public const string Logout = "/api/auth/logout";
        public const string ChangePassword = "/api/auth/change-password";
        public const string Me = "/api/auth/me";
    }

    public static class Users
    {
        public const string List = "/api/users";
        public const string Base = "/api/users";
        public static string ById(long id) => $"/api/users/{id}";
        public static string Activate(long id) => $"/api/users/{id}/activate";
        public static string ResetPassword(long id) => $"/api/users/{id}/reset-password";
    }

    public static class Roles
    {
        public const string Base = "/api/roles";
        public const string Permissions = "/api/permissions";
        public static string ById(long id) => $"/api/roles/{id}";
    }

    public static class Settings
    {
        public const string Base = "/api/settings";
    }

    public static class Audit
    {
        public const string Base = "/api/audit";
        public const string Export = "/api/audit/export.xlsx";
        public const string Actions = "/api/audit/actions";
    }

    public static class Products
    {
        public const string Base = "/api/products";
        public static string ById(long id) => $"/api/products/{id}";
        public static string Image(long id) => $"/api/products/{id}/image";
        public const string Uoms = "/api/uoms";
        public const string Categories = "/api/categories";
        public const string Export = "/api/products/export.xlsx";
    }

    public static class Printers
    {
        public const string Base = "/api/printers";
        public static string ById(long id) => $"/api/printers/{id}";
        public static string SetDefault(long id) => $"/api/printers/{id}/default";
        public static string Test(long id) => $"/api/printers/{id}/test";
    }

    public static class Print
    {
        public const string Base = "/api/print";
        public const string Jobs = "/api/print/jobs";
        public const string Reprint = "/api/print/jobs/reprint";
        public const string History = "/api/print/history";
        public const string Preview = "/api/print/preview";
        public const string Pending = "/api/print/pending";
        public static string JobById(long id) => $"/api/print/jobs/{id}";
        public static string Cancel(long id) => $"/api/print/jobs/{id}/cancel";
        public static string Payload(long id) => $"/api/print/jobs/{id}/payload";
        public static string Status(long id) => $"/api/print/jobs/{id}/status";

        /// <summary>Live job status (B-16). The print screen renders every push
        /// instead of re-fetching, so a job that fails 20 seconds after submit
        /// is visible without the operator doing anything.</summary>
        public const string Hub = "/hubs/print";
        public static string Claim(long id) => $"/api/print/jobs/{id}/claim";
    }

    public static class Dashboard
    {
        public const string Base = "/api/dashboard";
    }

    public static class Reports
    {
        public const string Base = "/api/reports";
        public const string Export = "/api/reports/export.xlsx";
    }

    public static class Templates
    {
        public const string Base = "/api/templates";
        public const string Vocabulary = "/api/templates/vocabulary";
        public static string ById(long id) => $"/api/templates/{id}";
        public static string Artifact(long id) => $"/api/templates/{id}/artifact";
        public static string Fields(long id) => $"/api/templates/{id}/fields";
        public static string Activate(long id) => $"/api/templates/{id}/activate";
        public static string SetDefault(long id) => $"/api/templates/{id}/default";
        public static string PreviewZpl(long id) => $"/api/templates/{id}/preview.zpl";
    }

    public static class Imports
    {
        public const string Base = "/api/imports";
        public const string Template = "/api/imports/template.xlsx";
        public const string Recent = "/api/imports/recent";
        public static string ById(long id) => $"/api/imports/{id}";
        public static string Errors(long id) => $"/api/imports/{id}/errors.xlsx";
        public static string Cancel(long id) => $"/api/imports/{id}/cancel";
        public const string Hub = "/hubs/imports";
    }
}
