using ResuClean.Data;
using ResuClean.Services;
using ResuClean.Services.Providers;
using ResuClean.Services.Sources;

namespace ResuClean.Tests;

/// <summary>
/// Spins up a real database in a temp directory so services are exercised as they run in production,
/// without touching the user's data folder.
/// </summary>
public sealed class TestHost : IDisposable
{
    public TestHost()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "resu-clean-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(DataDir);
        Paths.DataDir = DataDir;

        AppConfig = new Configuration.AppConfig
        {
            DataDir = DataDir,
            CacheTtlSeconds = 0,
            SourceMinIntervalMs = 0,
            MaxUploadBytes = 10 * 1024 * 1024,
            MaxConcurrency = 2
        };

        Db = new Db(AppConfig);
        Profile = new ProfileService(Db);
        ProviderRegistry = new ProviderService(Db, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Router = new ModelRouter(Db, ProviderRegistry);
        Store = new ResumeStore(Db, AppConfig);
        Registry = new SourceRegistry(Db);
        Http = new SourceHttp(Db, AppConfig);
        Jobs = new JobSearchService(Db, Registry, Http);
        App = new ApplicationService(Db, Jobs, Store, Profile, Router);
    }

    public string DataDir { get; }
    public Configuration.AppConfig AppConfig { get; }
    public Db Db { get; }
    public ProfileService Profile { get; }
    public ProviderService ProviderRegistry { get; }
    public ModelRouter Router { get; }
    public ResumeStore Store { get; }
    public SourceRegistry Registry { get; }
    public SourceHttp Http { get; }
    public JobSearchService Jobs { get; }
    public ApplicationService App { get; }

    /// <summary>Runs one SQL statement against the test database.</summary>
    public void Execute(string sql, params Microsoft.Data.Sqlite.SqliteParameter[] parameters)
    {
        using var conn = Db.Open();
        Db.Execute(conn, sql, parameters);
    }

    /// <summary>A sample resume used across several tests.</summary>
    public static string SampleResume => """
        JANE DOE
        jane.doe@example.com | +254700000000 | Nairobi, Kenya
        linkedin.com/in/janedoe

        SUMMARY
        Data analyst with 6 years of experience in financial reporting and reporting automation.

        EXPERIENCE
        Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
        - Led the migration of 14 monthly reports to Power BI, cutting reporting time from 9 days to 2.
        - Reduced reconciliation errors by 38% by introducing automated validation on 1,200 transactions.
        - Managed a team of 4 analysts and the reporting calendar for 3 business units.

        Data Analyst | Finlay Analytics | Jun 2018 - Feb 2021
        - Built SQL ETL pipelines over 5 million rows of transaction data.
        - Automated the regulatory reporting pack, saving 30 hours per month.
        - Partnered with finance on variance analysis for KES 400M of spend.

        EDUCATION
        BSc Statistics, University of Nairobi, 2014 - 2018

        SKILLS
        SQL, Power BI, Excel, Python, dbt, Airflow, stakeholder management
        """;

    public void Dispose()
    {
        try
        {
            Db.Dispose();
            if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
        }
        catch
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }
}