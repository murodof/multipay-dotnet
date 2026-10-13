using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Core.Dependencies;

public static class ClientIp
{
    public static string Of(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "";
        }
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}

public sealed class ForwardedHeadersSetup(IOptions<Settings> settings) : IConfigureOptions<ForwardedHeadersOptions>
{
    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = null;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var entry in settings.Value.FORWARDED_ALLOW_IPS.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0))
        {
            if (entry == "*")
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("0.0.0.0/0"));
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("::/0"));
            }
            else if (entry.Contains('/', StringComparison.Ordinal) && System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    options.KnownProxies.Add(address.MapToIPv6());
                }
            }
        }
    }
}
