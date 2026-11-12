using System;
using System.IO;

namespace OrderFlowTools
{
    /// <summary>
    /// A4 (Finding 4, Recheck): Token-Lebenszyklus mit EXKLUSIVEM, eigentümergesichertem Konsum.
    /// Ein bloß übereinstimmender Token-String genügt nicht, wenn eine fremde Recorder-Instanz ihn ebenfalls
    /// lesen kann. Deshalb:
    ///   - Publish schreibt das Token ATOMAR (temp -> rename), nie partiell.
    ///   - Consume beansprucht das Token per ATOMAREM Rename; genau EIN Konsument gewinnt, alle anderen bekommen null.
    ///   - RetractIfOwner löscht die Token-Datei nur, wenn ihr aktueller Inhalt dem eigenen Token entspricht
    ///     (kein versehentliches Entfernen eines fremden Auftragstokens).
    /// Die verbleibende Grenze (chart-gebundene Zustellung) ist nur live verifizierbar und bleibt offen dokumentiert.
    /// </summary>
    public static class TokenHandshake
    {
        public const string TokenFileName = ".of_run_token";

        public static string TokenPath(string root) => Path.Combine(root, TokenFileName);

        /// <summary>Steuerungslauf: Token atomar bereitstellen (temp -> rename), genau ein gültiger Stand.</summary>
        public static void Publish(string root, string token)
        {
            var path = TokenPath(root);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, token);
            File.Move(tmp, path, overwrite: true);   // atomar: nie ein halb geschriebener Token sichtbar
        }

        /// <summary>
        /// Recorder: Token EXKLUSIV beanspruchen. Das atomare Rename gelingt nur dem ersten Konsumenten;
        /// jeder weitere findet die Quelle nicht mehr und erhält null (kein doppelter Konsum desselben Tokens).
        /// </summary>
        public static string? Consume(string root, string claimId)
        {
            var path = TokenPath(root);
            var mine = path + ".claimed." + claimId;
            try { File.Move(path, mine); }           // wirft, wenn bereits konsumiert/abwesend -> Exklusivität
            catch { return null; }
            try
            {
                var t = File.ReadAllText(mine).Trim();
                return string.IsNullOrWhiteSpace(t) ? null : t;
            }
            catch { return null; }
            finally { try { File.Delete(mine); } catch { } }
        }

        /// <summary>Steuerungslauf-Abbruch: Token nur löschen, wenn WIR noch Eigentümer sind (Inhaltsvergleich).</summary>
        public static void RetractIfOwner(string root, string token)
        {
            var path = TokenPath(root);
            try
            {
                if (File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), token?.Trim(), StringComparison.Ordinal))
                    File.Delete(path);
            }
            catch { }
        }
    }
}
