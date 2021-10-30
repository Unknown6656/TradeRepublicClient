using System.Text;
using System.Text.RegularExpressions;

using OpenQA.Selenium;
using OpenQA.Selenium.DevTools.V85.DeviceOrientation;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;

namespace TradeRepublic;


/// <summary>
/// Represents the main TradeRepublic API.
/// Use the function <see cref="CreateAPIConnection"/> to create a new connection to TradeRepublic
/// </summary>
public static class API
{
    public static string BaseURL { get; set; } = "https://app.traderepublic.com";
    public static string TemporaryDirectory { get; set; } = "./temporary-files";

    private static readonly object _mutex = new();
    private static APIConnection? _connector = null;


    /// <summary>
    /// Creates a new API connection or returns the existing API connection if it has been created before.
    /// The API connection can be used to connect to TradeRepublic using an MSEdge Web Driver.
    /// </summary>
    /// <returns>An <see cref="APIConnection"/> instance</returns>
    public static APIConnection CreateAPIConnection()
    {
        lock (_mutex)
        {
            if (_connector is null)
            {
                DirectoryInfo temp_dir = new(TemporaryDirectory);

                if (!temp_dir.Exists)
                    temp_dir.Create();

                FileInfo msedge = Locate("msedge.exe") ?? new("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe");
                FileInfo msedgedriver = new(Path.Combine(temp_dir.FullName, "msedgedriver.exe"));

                if (!msedgedriver.Exists)
                    using (FileStream fs = msedgedriver.OpenWrite())
                        fs.Write(new(Resources.msedgedriver));

                EdgeOptions options = new()
                {
                    AcceptInsecureCertificates = false,
                    BinaryLocation = msedge.FullName,
                    LeaveBrowserRunning = false,
                };
                EdgeDriver driver = new EdgeDriver(temp_dir.FullName, options);

                _connector = new(driver, temp_dir, BaseURL);
            }

            return _connector;
        }
    }

    /// <summary>
    /// Closes an existing API connection (if any) and disposes the used resouces.
    /// </summary>
    public static void CloseAPIConnection() => Interlocked.Exchange(ref _connector, null)?.Dispose();

    private static FileInfo? Locate(string name)
    {
        var exts = Environment.GetEnvironmentVariable("PATHEXT").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var path = Environment.GetEnvironmentVariable("PATH").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        var candidates = from p in path
                         let dir = new DirectoryInfo(p)
                         where dir.Exists
                         from file in (new Func<IEnumerable<FileInfo>>(delegate
                         {
                             try
                             {
                                 return dir.EnumerateFiles();
                             }
                             catch
                             {
                                 return Enumerable.Empty<FileInfo>();
                             }
                         }))()
                         where exts.Contains(file.Extension, StringComparer.InvariantCultureIgnoreCase)
                         let filename = Path.GetFileNameWithoutExtension(file.Name)
                         where new[] { name, Path.GetFileNameWithoutExtension(name) }.Contains(filename, StringComparer.InvariantCultureIgnoreCase)
                         select file;

        return candidates.FirstOrDefault();
    }
}

public sealed class APIConnection
    : IDisposable
{
    private EdgeDriver _driver;
    private DirectoryInfo _temp_dir;
    private string _baseURL;


    private static readonly int[] accepted_country_codes =
    {
        30,
        31,
        32,
        33,
        34,
        36,
        39,
        40,
        43,
        45,
        46,
        48,
        49,
        351,
        352,
        353,
        356,
        357,
        358,
        359,
        370,
        371,
        372,
        385,
        386,
        420,
        421,
    };

    private const string URI__LOGIN = "/login#layout__main";
    private static readonly Regex REGEX__PHONE_NUMBER = new($@"(\+|00)(?<country>{string.Join('|', accepted_country_codes)})0?(?<number>\d+)", RegexOptions.Compiled);
    private static readonly By SEL__COOKIE_ACCPET_BUTTON = By.CssSelector(".app__dataConsent button.consentCard__action");
    private static readonly By SEL__PHONE_NUMBER_INPUT = By.Id("loginPhoneNumber__input");
    private static readonly By SEL__PIN_LOGIN_FIELD = By.CssSelector(".loginPin__field input[type=\"password\"]");
    private static readonly By SEL__SMS_LOGIN_FIELD = By.CssSelector(".smsCode__field input[type=\"text\"]");
    private const string SEL__COUNTRY_CODE_OPTION = "[id|=\"countryCode\"]";
    private const string SEL__COUNTRY_CODE_BUTTON = ".phoneNumberInput__countryCode button span";

    private const string ATTR__COUNTRY_CODE_OPTION_SELECTED = "aria-selected";

    public bool IsDisposed { get; private set; } = false;


    internal APIConnection(EdgeDriver driver, DirectoryInfo temp_dir, string baseURL)
    {
        _temp_dir = temp_dir;
        _baseURL = baseURL;
        _driver = driver;
    }

    private void InjectJQuery() => _driver.ExecuteScript(Resources.jquery);

    public void TryLogin(string phone, ushort pin)
    {
        phone = new string(phone.ToLower().Where(c => char.IsDigit(c) || c is '+').ToArray());

        Match match = REGEX__PHONE_NUMBER.Match(phone);
        int country = 0;

        if (!match.Success)
            throw new ArgumentException($"The phone number '{phone}' has not a valid number format. Did you forget the country code?", nameof(phone));
        if (!int.TryParse(match.Groups["country"].Value, out country))
            throw new ArgumentException($"Unrecognized country code '+{country}'. Did you type your number correctly?", nameof(phone));
        if (!accepted_country_codes.Contains(country))
            throw new ArgumentException($"Unsupported country code '+{country}'. Try again with an other phone number.", nameof(phone));

        phone = match.Groups["number"].Value;

        string code = Math.Min(pin, (ushort)9999).ToString("D4");

        _driver.Url = _baseURL + URI__LOGIN;
        _driver.Navigate();
        _driver.FindElement(SEL__COOKIE_ACCPET_BUTTON).Click();

        InjectJQuery();

        _driver.ExecuteScript($@"
            $('{SEL__COUNTRY_CODE_OPTION}').attr('{ATTR__COUNTRY_CODE_OPTION_SELECTED}', 'false');
            $('#countryCode-\+{country}').attr('{ATTR__COUNTRY_CODE_OPTION_SELECTED}', 'true');
            $('{SEL__COUNTRY_CODE_BUTTON}').html('+{country}');
        ");
        _driver.FindElement(SEL__PHONE_NUMBER_INPUT).Click();
        _driver.FindElement(SEL__PHONE_NUMBER_INPUT).SendKeys(phone + '\n');
        _driver.FindElement(SEL__PIN_LOGIN_FIELD).SendKeys(code);

        // check if correct pass
        // wait for sms callback
        // check if correct code
    }

    public void Dispose()
    {
        lock (this)
            if (!IsDisposed)
            {
                _driver.Close();
                _driver.Dispose();

                // TODO

                IsDisposed = true;
            }

        API.CloseAPIConnection();
    }
}
