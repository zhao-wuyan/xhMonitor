using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace XhMonitor.Desktop.Services;

internal sealed class IpWhitelistMatcher
{
    private readonly IpRule[] _rules;

    private IpWhitelistMatcher(IpRule[] rules)
    {
        _rules = rules;
    }

    public bool HasRules => _rules.Length > 0;

    public static IpWhitelistMatcher Parse(string whitelist)
    {
        if (string.IsNullOrWhiteSpace(whitelist))
        {
            return new IpWhitelistMatcher(Array.Empty<IpRule>());
        }

        var items = whitelist.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0)
        {
            return new IpWhitelistMatcher(Array.Empty<IpRule>());
        }

        var rules = new List<IpRule>(items.Length);
        foreach (var item in items)
        {
            if (TryParseRule(item, out var rule))
            {
                rules.Add(rule);
            }
        }

        return new IpWhitelistMatcher(rules.ToArray());
    }

    public bool IsAllowed(IPAddress clientAddress)
    {
        var clientBytes = clientAddress.GetAddressBytes();
        foreach (var rule in _rules)
        {
            if (rule.IsMatch(clientAddress.AddressFamily, clientBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseRule(string text, out IpRule rule)
    {
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            rule = default;
            return false;
        }

        if (!text.Contains('/'))
        {
            if (!IPAddress.TryParse(text, out var ip))
            {
                rule = default;
                return false;
            }

            var ipBytes = ip.GetAddressBytes();
            rule = IpRule.Exact(ip.AddressFamily, ipBytes);
            return true;
        }

        var parts = text.Split('/');
        if (parts.Length != 2)
        {
            rule = default;
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var networkAddress))
        {
            rule = default;
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixLength))
        {
            rule = default;
            return false;
        }

        var addressBytes = networkAddress.GetAddressBytes();
        var totalBits = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > totalBits)
        {
            rule = default;
            return false;
        }

        rule = IpRule.Cidr(networkAddress.AddressFamily, addressBytes, prefixLength);
        return true;
    }

    private readonly struct IpRule
    {
        private readonly AddressFamily _addressFamily;
        private readonly byte[] _networkMaskedBytes;
        private readonly byte[] _maskBytes;

        private IpRule(AddressFamily addressFamily, byte[] networkMaskedBytes, byte[] maskBytes)
        {
            _addressFamily = addressFamily;
            _networkMaskedBytes = networkMaskedBytes;
            _maskBytes = maskBytes;
        }

        public static IpRule Exact(AddressFamily addressFamily, byte[] addressBytes)
        {
            var mask = new byte[addressBytes.Length];
            Array.Fill(mask, (byte)0xFF);

            var networkMasked = new byte[addressBytes.Length];
            Buffer.BlockCopy(addressBytes, 0, networkMasked, 0, addressBytes.Length);
            return new IpRule(addressFamily, networkMasked, mask);
        }

        public static IpRule Cidr(AddressFamily addressFamily, byte[] networkBytes, int prefixLength)
        {
            var maskBytes = new byte[networkBytes.Length];
            for (int i = 0; i < maskBytes.Length; i++)
            {
                var bitsInByte = Math.Min(8, prefixLength - (i * 8));
                if (bitsInByte <= 0)
                {
                    maskBytes[i] = 0;
                }
                else if (bitsInByte >= 8)
                {
                    maskBytes[i] = 0xFF;
                }
                else
                {
                    maskBytes[i] = (byte)(0xFF << (8 - bitsInByte));
                }
            }

            var maskedNetwork = new byte[networkBytes.Length];
            for (int i = 0; i < networkBytes.Length; i++)
            {
                maskedNetwork[i] = (byte)(networkBytes[i] & maskBytes[i]);
            }

            return new IpRule(addressFamily, maskedNetwork, maskBytes);
        }

        public bool IsMatch(AddressFamily addressFamily, byte[] clientBytes)
        {
            if (_addressFamily != addressFamily || clientBytes.Length != _networkMaskedBytes.Length)
            {
                return false;
            }

            for (int i = 0; i < clientBytes.Length; i++)
            {
                if ((clientBytes[i] & _maskBytes[i]) != _networkMaskedBytes[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}

