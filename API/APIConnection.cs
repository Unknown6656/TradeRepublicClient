using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

using OpenQA.Selenium;
using OpenQA.Selenium.DevTools.V85.DeviceOrientation;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;
using OpenQA.Selenium.Support.UI;

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
                options.AddArgument("--disable-infobars");
                options.AddAdditionalCapability("useAutomationExtension", false);
                options.AddExcludedArgument("enable-automation");

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


    /// <summary>
    /// A collection of currently accepted country codes.
    /// </summary>
    public static readonly IReadOnlyCollection<int> accepted_country_codes = new int[]
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
    private const string URI__PORTFOLIO = "/portfolio?timeframe=1y";
    private static readonly Regex REGEX__PHONE_NUMBER = new($@"(\+|00)(?<country>{string.Join('|', accepted_country_codes)})0?(?<number>\d+)", RegexOptions.Compiled);
    private static readonly By SEL__COOKIE_ACCPET_BUTTON = By.CssSelector(".app__dataConsent button.consentCard__action");
    private static readonly By SEL__PHONE_NUMBER_INPUT = By.Id("loginPhoneNumber__input");
    private static readonly By SEL__PIN_LOGIN_FIELD = By.CssSelector(".loginPin__field input[type=\"password\"]");
    private static readonly By SEL__SMS_LOGIN_FIELD = By.CssSelector(".smsCode__field input[type=\"text\"]");
    private static readonly By SEL__PIN_LOGIN_ERROR = By.ClassName("loginPin__errorMessage");
    private static readonly By SEL__SMS_LOGIN_ERROR = By.ClassName("smsCode__statusMessage");
    private static readonly By SEL__LOGOUT_BUTTON = By.ClassName("settings__sessionControl");

    private const string SEL__COUNTRY_CODE_OPTION = "[id|=\"countryCode\"]";
    private const string SEL__COUNTRY_CODE_BUTTON = ".phoneNumberInput__countryCode button span";

    private const string ATTR__COUNTRY_CODE_OPTION_SELECTED = "aria-selected";

    /// <summary>
    /// Indicates whether the connection has been closed and/or disposed.
    /// </summary>
    public bool IsDisposed { get; private set; } = false;

    /// <summary>
    /// Indicates whether the user is currently logged in.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the API connection has already been closed or disposed.</exception>
    public bool IsLoggedIn => IsDisposed ? throw new ObjectDisposedException(nameof(APIConnection)) : WaitForElements(SEL__LOGOUT_BUTTON, 1_000) is { Length: > 0 };


    internal APIConnection(EdgeDriver driver, DirectoryInfo temp_dir, string baseURL)
    {
        _temp_dir = temp_dir;
        _baseURL = baseURL;
        _driver = driver;
    }

    private void InjectJQuery() => _driver.ExecuteScript(Resources.jquery); 

    /// <summary>
    /// Tries to log in using the given credentials. An <see cref="ArgumentException"/> will be thrown if the credentials are incorrect.
    /// </summary>
    /// <param name="phone">The phone number associated with the TradeRepublic account. The number must contain the country code (prefixed with a '+' or with '00').</param>
    /// <param name="pin">The four-digit pin (leading zeros may be omitted).</param>
    /// <param name="sms_callback">The callback, which is called if a SMS pin is requested. The callback should return the sent SMS pin.</param>
    /// <returns>Returns whether the login process has been successful.</returns>
    /// <!-- <exception cref="ArgumentException">Thrown if the credentials are invalid.</exception> -->
    /// <exception cref="ObjectDisposedException">Thrown if the API connection has already been closed or disposed.</exception>
    public LoginStatus Login(string phone, ushort pin, Func<ushort> sms_callback)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(APIConnection));

        phone = new string(phone.ToLower().Where(c => char.IsDigit(c) || c is '+').ToArray());

        Match match = REGEX__PHONE_NUMBER.Match(phone);
        int country = 0;

        if (!match.Success)
            return LoginStatus.Failure_InvalidPhoneNumber;
        if (!int.TryParse(match.Groups["country"].Value, out country))
            return LoginStatus.Failure_InvalidPhoneNumber;
        if (!accepted_country_codes.Contains(country))
            return LoginStatus.Failure_InvalidPhoneNumber;

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

        if (_driver.FindElement(SEL__PIN_LOGIN_ERROR).Text is string error && !string.IsNullOrWhiteSpace(error))
            return LoginStatus.Failure_WrongPIN;

        string sms_pin = Math.Min(sms_callback(), (ushort)9999).ToString("D4");
        int sms_index = 0;

        foreach (var input in _driver.FindElements(SEL__SMS_LOGIN_FIELD))
        {
            input.SendKeys(sms_pin[sms_index].ToString());
            ++sms_index;

            if (sms_index >= 4)
                break;
        }

        if (_driver.FindElement(SEL__SMS_LOGIN_ERROR).Text is string sms_error && !string.IsNullOrWhiteSpace(sms_error))
            return LoginStatus.Failure_WrongSMSCode;

        if (WaitForElements(SEL__LOGOUT_BUTTON, 30_000) is { Length: > 0 })
            return LoginStatus.Success;
        else
            return LoginStatus.Failure_LoginTimeout;
    }

    /// <summary>
    /// Tries to logout of the current session.
    /// <para/>
    /// This action does NOT close the current API connection. Use the <see cref="Dispose"/>-method for that.
    /// </summary>
    /// <returns>Returns whether a logout process has been performed. A value of <see langword="false"/> indicates that the user was already logged out.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the API connection has already been closed or disposed.</exception>
    public bool Logout()
    {
        bool logged_in = IsLoggedIn;

        if (logged_in)
        {
            _driver.FindElement(SEL__LOGOUT_BUTTON).Click();
            _driver.Url = _baseURL;
            _driver.Navigate();
        }

        return logged_in;
    }

    /// <summary>
    /// Waits for the given elements to be selectable (by the web driver) and returns a non-empty collection if any element has been found before the timeout.
    /// </summary>
    private IWebElement[]? WaitForElements(By selector, float ms_timeout = 10_000)
    {
        WebDriverWait wait = new(_driver, TimeSpan.FromMilliseconds(ms_timeout));
        IWebElement[]? elements = null;

        return wait.Until(_ =>
        {
            var result = _driver.FindElements(selector);

            if (result.Any())
            {
                elements = result.ToArray();

                return true;
            }
            else
                return false;
        });

        return elements;
    }

    /// <summary>
    /// Closes the API connection and disposes all underlying resources
    /// </summary>
    public void Close() => Dispose();

    /// <inheritdoc cref="Close"/>
    public void Dispose()
    {
        lock (this)
            if (!IsDisposed)
            {
                Logout();

                _driver.Close();
                _driver.Dispose();

                // TODO

                IsDisposed = true;
            }

        API.CloseAPIConnection();
    }
}

/// <summary>
/// An enumeration of possible login states.
/// </summary>
public enum LoginStatus
{
    /// <summary>
    /// The user has been successfully logged in.
    /// </summary>
    Success,
    /// <summary>
    /// The phone number is invalid.
    /// Did you check the following:
    /// <list type="bullet">
    ///     <item>Is the country code missing?</item>
    ///     <item>Is the phone number in the correct format? i.e. "+49 123 45 67 89", "+43 (0)1234 / 567 89", "0033 1 23 45 67 89"</item>
    ///     <item>Is the country code in the list of supported countries? Check <see cref="APIConnection.accepted_country_codes"/>.</item>
    /// </list>
    /// </summary>
    Failure_InvalidPhoneNumber,
    /// <summary>
    /// Wrong combination of phone number and PIN. Note: this may also indicate an invalid phone number.
    /// </summary>
    Failure_WrongPIN,
    /// <summary>
    /// Wrong SMS Code.
    /// </summary>
    Failure_WrongSMSCode,
    /// <summary>
    /// The login process has timed out.
    /// </summary>
    Failure_LoginTimeout,
}

