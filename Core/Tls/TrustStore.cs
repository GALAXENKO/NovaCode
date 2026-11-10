using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace NovaCode.Core.Tls
{
    /// <summary>
    /// Проверка цепочки сертификатов. Доверенные корни берутся из вшитого списка
    /// (в XP-хранилище нет новых корней) и дополнительно из хранилища Windows.
    /// </summary>
    public static class TrustStore
    {
        private static List<X509Cert> _roots;

        /// <summary>Доверенные корни: вшитые + системные. Загружаются один раз.</summary>
        public static List<X509Cert> Roots
        {
            get
            {
                if (_roots == null) _roots = LoadRoots();
                return _roots;
            }
        }

        private static List<X509Cert> LoadRoots()
        {
            var roots = new List<X509Cert>();

            foreach (string base64 in TrustStoreData.Roots)
            {
                if (string.IsNullOrEmpty(base64)) continue;

                try
                {
                    roots.Add(X509Cert.Parse(Convert.FromBase64String(base64)));
                }
                catch
                {
                    // Битый корень в списке не должен ломать весь клиент.
                }
            }

            foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                try
                {
                    // В net40 X509Store не реализует IDisposable — закрываем вручную.
                    var store = new X509Store(StoreName.Root, location);
                    try
                    {
                        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                        foreach (X509Certificate2 cert in store.Certificates)
                        {
                            try { roots.Add(X509Cert.Parse(cert.GetRawCertData())); }
                            catch { }
                        }
                    }
                    finally
                    {
                        store.Close();
                    }
                }
                catch
                {
                    // На XP доступ к хранилищу может быть ограничен — не критично.
                }
            }

            return roots;
        }

        /// <summary>
        /// Проверяет, что leaf выписан на host, сроки в порядке и цепочка
        /// сходится к доверенному корню.
        /// </summary>
        public static bool Validate(string host, List<X509Cert> chain, DateTime now, out string error)
        {
            error = "";

            if (chain == null || chain.Count == 0)
            {
                error = "сервер не прислал сертификат";
                return false;
            }

            X509Cert leaf = chain[0];

            if (!leaf.MatchesHost(host))
            {
                error = "сертификат выписан не на " + host + " (в нём: " +
                        (leaf.DnsNames.Count == 0 ? "нет имён" : string.Join(", ", leaf.DnsNames.ToArray())) + ")";
                return false;
            }

            for (int i = 0; i < chain.Count; i++)
            {
                if (chain[i].IsTimeValid(now)) continue;

                error = "истёк срок действия сертификата " + chain[i].SubjectText;
                return false;
            }

            return ValidateChain(chain, out error);
        }

        private static bool ValidateChain(List<X509Cert> chain, out string error)
        {
            error = "";

            for (int i = 0; i < chain.Count; i++)
            {
                X509Cert current = chain[i];

                // Подпись может стоять на следующем сертификате из присланных...
                if (i + 1 < chain.Count && current.VerifyWith(chain[i + 1]))
                    continue;

                // ...или на корне, которого в цепочке нет.
                X509Cert root = FindIssuer(current);
                if (root != null && current.VerifyWith(root))
                    continue;

                if (current.IsSelfIssued)
                {
                    error = "корневой сертификат не входит в список доверенных: " + current.SubjectText;
                    return false;
                }

                if (!current.HasKey)
                {
                    error = "подпись сертификата сделана ключом, который не поддерживается (" +
                            current.SignatureAlgorithm + "): " + current.SubjectText;
                    return false;
                }

                error = "не удалось проверить подпись сертификата " + current.SubjectText;
                return false;
            }

            return true;
        }

        /// <summary>Ищет доверенный корень, которым подписан сертификат.</summary>
        private static X509Cert FindIssuer(X509Cert cert)
        {
            List<X509Cert> roots = Roots;

            for (int i = 0; i < roots.Count; i++)
            {
                X509Cert root = roots[i];
                if (!root.HasKey) continue;
                if (!Secret.Equal(root.SubjectRaw, cert.IssuerRaw)) continue;
                return root;
            }

            return null;
        }
    }
}
