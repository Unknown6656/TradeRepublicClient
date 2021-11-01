using System.Diagnostics;

using TradeRepublic;


using var connection = API.CreateAPIConnection();

await connection.Login("+491776200214", 1488, () =>
{
    Thread.Sleep(500);
    Console.Clear();
    Console.WriteLine("please enter the sms pin:");
    return ushort.Parse(Console.ReadLine() ?? "0");
});
Console.WriteLine(await connection.FetchPersonalData());
Console.WriteLine("press ENTER to log out.");
Console.ReadLine();
connection.Logout();

Debugger.Break();