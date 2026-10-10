using System.Net;
using System.Text;

namespace Cocorra.BLL.Services.Email
{
    /// <summary>
    /// Branded HTML templates for every transactional Cocorra email.
    /// All templates share one <see cref="Layout"/> so the design lives in a single place.
    /// The markup is email-client safe: table layout and inline styles only — no flexbox,
    /// no viewport units and no reliance on a &lt;style&gt; block, which Outlook and some
    /// Gmail views ignore or strip.
    /// Every interpolated value is HTML-encoded (user-controlled names/emails must never inject markup).
    /// </summary>
    public static class EmailTemplates
    {
        /// <summary>
        /// Logo shown in the header of every email. Override with <c>EmailSettings:LogoUrl</c>.
        /// </summary>
        public const string DefaultLogoUrl = "https://admin.cocorraapp.com/cocorra-logo.jpg";

        /// <summary>Returns the configured logo URL, or <see cref="DefaultLogoUrl"/> when none is set.</summary>
        public static string ResolveLogoUrl(string? configuredUrl) =>
            string.IsNullOrWhiteSpace(configuredUrl) ? DefaultLogoUrl : configuredUrl.Trim();

        // Brand palette, shared with wwwroot/Html pages.
        private const string PageBackground = "#e0e0e0";
        private const string HeaderBackground = "#4f5b49";
        private const string LogoBackground = "#c5d1ba";
        private const string ContentBackground = "#a0b19d";
        private const string FontStack = "'Segoe UI', Tahoma, Geneva, Verdana, Arial, sans-serif";

        private const string Signoff = "— فريق كوكورا (The Cocorra Team)";

        /// <summary>
        /// OTP verification template — used during registration and resend-OTP flows.
        /// </summary>
        public static string Otp(string userName, string email, string otpCode, string logoUrl) =>
            Layout(
                title: "تأكيد البريد الإلكتروني",
                preheader: "كود التحقق الخاص بحسابك في كوكورا",
                logoUrl: logoUrl,
                body:
                    Greeting($"مرحباً {Encode(userName)}") +
                    EmailPill(email) +
                    Paragraph("كود التحقق لتأكيد بريدك الإلكتروني<br>وإتمام تسجيل حسابك في كوكورا هو:") +
                    CodeBox(otpCode) +
                    Footer("هذا الكود صالح لمدة 6 دقائق (valid for 6 minutes)."));

        /// <summary>
        /// Password-reset template — sends a one-time code to reset the user's password.
        /// </summary>
        public static string PasswordReset(string userName, string email, string otpCode, string logoUrl) =>
            Layout(
                title: "إعادة تعيين كلمة المرور",
                preheader: "كود إعادة تعيين كلمة المرور الخاصة بك",
                logoUrl: logoUrl,
                body:
                    Greeting($"مرحباً {Encode(userName)}") +
                    EmailPill(email) +
                    Paragraph("استخدم الكود أدناه لإعادة تعيين كلمة المرور الخاصة بك.<br>إذا لم تطلب هذا الرمز، يمكنك تجاهل هذا البريد بأمان.") +
                    CodeBox(otpCode) +
                    Footer("هذا الكود صالح لمدة 6 دقائق (valid for 6 minutes)."));

        /// <summary>
        /// Sent when an account moves to Pending — the voice sample was received and awaits review.
        /// </summary>
        public static string VerificationPending(string userName, string logoUrl) =>
            Layout(
                title: "تم استلام العينة الصوتية",
                preheader: "طلبك قيد المراجعة من قبل فريق كوكورا",
                logoUrl: logoUrl,
                body:
                    Greeting($"مرحباً {Encode(userName)}،") +
                    Paragraph("شكراً لتقديمك عينة التحقق الصوتي. تم استلام طلبك وهو قيد المراجعة حالياً من قبل فريق العمل.") +
                    Paragraph("سنقوم بإشعارك فور اتخاذ القرار، وعادةً ما يستغرق ذلك من 24 إلى 48 ساعة.") +
                    Footer(Signoff));

        /// <summary>
        /// Sent when an account is approved (Active) — welcome email.
        /// </summary>
        public static string AccountVerified(string userName, string logoUrl) =>
            Layout(
                title: "تم توثيق حسابك",
                preheader: "أهلاً بك في كوكورا — تم توثيق حسابك بنجاح",
                logoUrl: logoUrl,
                body:
                    Greeting($"أهلاً بك يا {Encode(userName)}!") +
                    Badge("✅ تم توثيق حسابك (Verified)") +
                    Paragraph("تمت الموافقة على عينة التحقق الصوتي الخاصة بك. أصبح بإمكانك الآن الاستفادة الكاملة من تطبيق كوكورا — استكشاف الغرف، المشاركة في المحادثات، والتواصل مع الجميع.") +
                    Paragraph("يسعدنا وجودك معنا في كوكورا!") +
                    Footer(Signoff));

        /// <summary>
        /// Sent when an account moves to ReRecord — the voice sample was not accepted.
        /// </summary>
        public static string ReRecordRequired(string userName, string logoUrl) =>
            Layout(
                title: "مطلوب إعادة تسجيل العينة الصوتية",
                preheader: "نحتاج منك إرسال عينة صوتية جديدة لتفعيل حسابك",
                logoUrl: logoUrl,
                body:
                    Greeting($"مرحباً {Encode(userName)}،") +
                    Paragraph("قمنا بمراجعة عينة التحقق الصوتي الخاصة بك، ولكن للأسف لم نتمكن من اعتمادها. قد يرجع ذلك إلى جودة الصوت، أو وجود ضوضاء في الخلفية، أو عدم وضوح التسجيل.") +
                    Paragraph("<strong>يرجى فتح التطبيق وإرسال عينة صوتية جديدة</strong> لنتمكن من إتمام عملية التوثيق وتفعيل حسابك.") +
                    Footer(Signoff));

        // ── Shared layout ───────────────────────────────────────────────────

        /// <summary>
        /// The single Cocorra email shell: grey page, olive header with the logo, sage content card.
        /// <paramref name="body"/> is trusted markup built from the component helpers below;
        /// <paramref name="title"/>, <paramref name="preheader"/> and <paramref name="logoUrl"/> are encoded here.
        /// </summary>
        private static string Layout(string title, string preheader, string logoUrl, string body)
        {
            title = Encode(title);
            preheader = Encode(preheader);
            logoUrl = Encode(logoUrl);

            var html = new StringBuilder();
            html.Append($$"""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <meta name="color-scheme" content="light">
                <title>{{title}}</title>
            </head>
            <body style="margin:0; padding:0; background-color:{{PageBackground}};">
                <div style="display:none; max-height:0; overflow:hidden; mso-hide:all;">{{preheader}}</div>
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" bgcolor="{{PageBackground}}" style="background-color:{{PageBackground}};">
                    <tr>
                        <td align="center" style="padding:24px 12px;">
                            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" dir="rtl" style="max-width:480px; width:100%; border-collapse:collapse;">
                                <tr>
                                    <td align="center" bgcolor="{{HeaderBackground}}" style="background-color:{{HeaderBackground}}; padding:30px 0; border-radius:8px 8px 0 0;">
                                        <img src="{{logoUrl}}" alt="Cocorra" width="100" height="100" style="display:block; width:100px; height:100px; border:2px solid #ffffff; border-radius:8px; background-color:{{LogoBackground}}; object-fit:cover;">
                                    </td>
                                </tr>
                                <tr>
                                    <td align="center" bgcolor="{{ContentBackground}}" style="background-color:{{ContentBackground}}; padding:25px 20px 40px; border-radius:0 0 8px 8px; font-family:{{FontStack}}; color:#ffffff; direction:rtl; text-align:center;">
            """);
            html.Append(body);
            html.Append("""
                                    </td>
                                </tr>
                            </table>
                        </td>
                    </tr>
                </table>
            </body>
            </html>
            """);
            return html.ToString();
        }

        // ── Components ──────────────────────────────────────────────────────
        // Each takes already-safe markup (callers Encode user input) except where noted.

        private static string Greeting(string safeText) =>
            $"<h1 style=\"margin:0 0 15px 0; font-family:{FontStack}; font-size:26px; font-weight:bold; color:#ffffff;\">{safeText}</h1>";

        /// <summary>White pill showing the recipient's address. Encodes <paramref name="email"/>.</summary>
        private static string EmailPill(string email) =>
            $"<div dir=\"ltr\" style=\"background-color:#ffffff; color:#333333; padding:12px 20px; border-radius:30px; font-family:{FontStack}; font-weight:bold; font-size:18px; margin:0 auto 25px; max-width:85%; word-break:break-all;\">{Encode(email)}</div>";

        private static string Paragraph(string safeHtml) =>
            $"<p style=\"margin:0 0 20px 0; font-family:{FontStack}; font-size:16px; line-height:1.6; font-weight:600; color:#ffffff;\">{safeHtml}</p>";

        /// <summary>Large one-time code box. Encodes <paramref name="code"/>.</summary>
        private static string CodeBox(string code) =>
            $"<div dir=\"ltr\" style=\"background-color:#ffffff; color:#000000; font-family:{FontStack}; font-size:32px; font-weight:900; letter-spacing:8px; padding:20px; border-radius:15px; margin:5px auto 25px; max-width:85%;\">{Encode(code)}</div>";

        private static string Badge(string safeText) =>
            $"<div style=\"display:inline-block; background-color:{HeaderBackground}; color:#ffffff; font-family:{FontStack}; font-size:15px; font-weight:bold; padding:8px 18px; border-radius:20px; margin:0 0 20px 0;\">{safeText}</div>";

        private static string Footer(string safeText) =>
            $"<p style=\"margin:10px 0 0 0; font-family:{FontStack}; font-size:14px; font-weight:bold; color:#ffffff;\">{safeText}</p>";

        private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
