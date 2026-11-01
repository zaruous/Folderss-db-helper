using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MyPlugin
{
    /// <summary>
    /// SQL Server 접속별 암호화·서버 인증서 검증(프로필의 SqlEncrypt·SqlTrustServerCertificate·SqlHostNameInCertificate).
    /// 값이 없으면(새 접속) 암호화 필수 + 인증서 검증. 자체 서명 인증서는 [서버 인증서 신뢰]를 직접 켜야 붙는다.
    /// 이 설정이 없던 이전 버전에서 저장한 SQL Server 접속은 읽을 때 이전 동작(Optional + 신뢰)으로 채우고 화면에서 강화를 권한다(<see cref="ApplyLegacy"/>).
    /// </summary>
    internal static class SqlServerSecurity
    {
        public const string Optional = "optional";
        public const string Mandatory = "mandatory";
        public const string Strict = "strict";

        /// <summary>화면 선택지 순서(권장 순).</summary>
        public static readonly string[] Modes = { Mandatory, Strict, Optional };

        public const string UntrustedCertificate = "서버 인증서를 신뢰할 수 없습니다 — 자체 서명이면 [서버 인증서 신뢰]를 켜세요.";
        public const string CertificateNameMismatch = "서버 인증서의 이름이 호스트와 다릅니다 — [인증서 이름]에 인증서의 이름을 넣거나, 자체 서명이면 [서버 인증서 신뢰]를 켜세요.";

        // SEC_E_UNTRUSTED_ROOT(0x80090325)·SEC_E_WRONG_PRINCIPAL(0x80090322)·CERT_E_CN_NO_MATCH(0x800B010F)
        private const int UntrustedRoot = unchecked((int)0x80090325);
        private const int WrongPrincipal = unchecked((int)0x80090322);
        private const int NameNoMatch = unchecked((int)0x800B010F);

        /// <summary>알려진 값이면 소문자 이름, 아니면 null.</summary>
        public static string Normalize(string mode)
        {
            var m = (mode ?? "").Trim().ToLowerInvariant();
            return Modes.Contains(m) ? m : null;
        }

        /// <summary>쓸 암호화 모드. 없거나 모르는 값이면 필수.</summary>
        public static string EncryptOf(OracleConnectionProfile p)
        {
            return Normalize(p == null ? null : p.SqlEncrypt) ?? Mandatory;
        }

        /// <summary>서버 인증서를 검증 없이 신뢰하는지. 엄격(Strict)은 항상 검증하므로 false.</summary>
        public static bool TrustOf(OracleConnectionProfile p)
        {
            return p != null && EncryptOf(p) != Strict && p.SqlTrustServerCertificate == true;
        }

        public static string HostNameOf(OracleConnectionProfile p)
        {
            return p == null ? "" : (p.SqlHostNameInCertificate ?? "").Trim();
        }

        public static bool IsSqlServer(OracleConnectionProfile p)
        {
            return p != null && string.Equals(p.Provider, SqlServerProvider.KindName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 읽을 때(JSON → 프로필, 공급자 정리 뒤): 암호화 값이 없는 SQL Server 접속은 이 설정이 생기기 전에 저장된 것이므로
        /// 기존 사용자가 갑자기 붙지 못하는 일이 없게 이전 동작(Optional + 인증서 신뢰)으로 채운다. 모르는 값은 필수로.
        /// </summary>
        public static void ApplyLegacy(OracleConnectionProfile p)
        {
            if (!IsSqlServer(p))
                return;
            if (p.SqlEncrypt == null)
            {
                p.SqlEncrypt = Optional;
                if (p.SqlTrustServerCertificate == null)
                    p.SqlTrustServerCertificate = true;
            }
            else
            {
                p.SqlEncrypt = EncryptOf(p);
            }
        }

        /// <summary>저장할 값: SQL Server면 실제로 쓰는 값을 명시(빈 값으로 남기면 다음에 읽을 때 이전 형식으로 보므로), 아니면 모두 null.</summary>
        public static void Materialize(OracleConnectionProfile p)
        {
            if (IsSqlServer(p))
            {
                p.SqlEncrypt = EncryptOf(p);
                p.SqlTrustServerCertificate = p.SqlTrustServerCertificate == true;
                var host = HostNameOf(p);
                p.SqlHostNameInCertificate = host.Length == 0 ? null : host;
            }
            else
            {
                p.SqlEncrypt = null;
                p.SqlTrustServerCertificate = null;
                p.SqlHostNameInCertificate = null;
            }
        }

        public static string Title(string mode)
        {
            switch (Normalize(mode))
            {
                case Strict:
                    return "엄격 (Strict, TDS 8.0)";
                case Optional:
                    return "선택 (Optional — 서버가 요구할 때만)";
                default:
                    return "필수 (Mandatory)";
            }
        }

        /// <summary>약한 설정이면 강화를 권하는 안내, 아니면 null.</summary>
        public static string Hint(OracleConnectionProfile p)
        {
            if (!IsSqlServer(p))
                return null;
            if (EncryptOf(p) == Optional)
                return "암호화가 필수가 아닙니다(이전 버전의 기본값). 운영 접속은 [필수]와 인증서 검증을 권장합니다.";
            if (TrustOf(p))
                return "서버 인증서를 검증하지 않습니다(자체 서명 예외). 운영 접속은 신뢰할 수 있는 인증서로 검증하세요.";
            return null;
        }

        /// <summary>인증서 호스트 이름 검사. 문제가 없으면 빈 목록.</summary>
        public static List<string> Validate(OracleConnectionProfile p, string label)
        {
            var errors = new List<string>();
            var host = HostNameOf(p);
            if (host.Any(c => char.IsWhiteSpace(c) || c == ';' || c == '=' || c == '\'' || c == '"'))
                errors.Add(label + ": [인증서 이름]에 공백·';'·'='·따옴표를 넣을 수 없습니다.");
            return errors;
        }

        public static void Apply(SqlConnectionStringBuilder builder, OracleConnectionProfile p)
        {
            switch (EncryptOf(p))
            {
                case Strict:
                    builder.Encrypt = SqlConnectionEncryptOption.Strict;
                    break;
                case Optional:
                    builder.Encrypt = SqlConnectionEncryptOption.Optional;
                    break;
                default:
                    builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                    break;
            }
            builder.TrustServerCertificate = TrustOf(p);
            var host = HostNameOf(p);
            if (host.Length > 0)
                builder.HostNameInCertificate = host;
        }

        /// <summary>서버 인증서 검증 실패면 안내 문장(이름 불일치·신뢰할 수 없음), 아니면 null.</summary>
        public static string CertificateProblem(Exception ex)
        {
            var untrusted = false;
            for (var e = ex; e != null; e = e.InnerException)
            {
                var number = e is SqlException sql ? sql.Number : e.HResult;
                var message = e.Message ?? "";
                if (number == WrongPrincipal || number == NameNoMatch
                    || Contains(message, "principal name is incorrect") || Contains(message, "RemoteCertificateNameMismatch")
                    || Contains(message, "name on the certificate"))
                    return CertificateNameMismatch;
                if (number == UntrustedRoot
                    || (e is AuthenticationException && Contains(message, "certificate"))
                    || Contains(message, "certificate chain") || Contains(message, "RemoteCertificateChainErrors")
                    || Contains(message, "certificate is not trusted"))
                    untrusted = true;
                if (e is AggregateException agg && agg.InnerExceptions.Count > 0)
                {
                    var inner = CertificateProblem(agg.InnerExceptions[0]);
                    if (inner != null)
                        return inner;
                }
            }
            return untrusted ? UntrustedCertificate : null;
        }

        private static readonly Regex SecretPair = new Regex(
            @"\b(password|pwd)\s*=\s*(""(?:[^""]|"""")*""|'(?:[^']|'')*'|[^;]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>오류·로그에 보일 글에서 연결 문자열의 비밀번호(Password=…·Pwd=…)와 입력한 비밀번호 자체를 가린다.</summary>
        public static string Redact(string text, string password = null)
        {
            if (string.IsNullOrEmpty(text))
                return text ?? "";
            var result = SecretPair.Replace(text, m => m.Groups[1].Value + "=***");
            if (!string.IsNullOrEmpty(password) && password.Length >= 3)
                result = result.Replace(password, "***");
            return result;
        }

        private static bool Contains(string text, string part)
        {
            return text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
