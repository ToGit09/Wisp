using MimeKit;
using Wisp.Core.Smart;
using Wisp.Core.Storage;

namespace Wisp.Core.Tests;

public class OneTimeCodeTests
{
    [Theory]
    [InlineData("Ihr Bestätigungscode", "Ihr Bestätigungscode lautet 482913. Er ist 10 Minuten gültig.", "482913")]
    [InlineData("Anmeldung bei Konto", "Verwenden Sie den folgenden Code: 739 104", "739104")]
    [InlineData("G-552190 ist dein Google-Bestätigungscode", null, "552190")]
    [InlineData("Your verification code", "Your verification code is 9081. Do not share it.", "9081")]
    [InlineData("Login", "Enter this one-time passcode to sign in: 30417265", "30417265")]
    [InlineData("TAN für Ihre Überweisung", "Ihre TAN: 638271", "638271")]
    [InlineData("Security code", "Security code: 4815-1623", "48151623")]
    public void FindsCodes(string subject, string? text, string expected) =>
        Assert.Equal(expected, OneTimeCode.Find(subject, text));

    [Theory]
    [InlineData("Ihre Bestellung 4839201", "Vielen Dank für Ihre Bestellung Nr. 4839201 vom 12.03.2026.")]
    [InlineData("10 % Rabatt", "Ihr Gutscheincode 582910 gilt bis Sonntag.")]
    [InlineData("Rechnung", "Rechnungsnummer 772341, Betrag 4500 EUR")]
    [InlineData("Treffen morgen", "Wir sehen uns um 14:30 in Raum 1204.")]
    [InlineData("Newsletter", "Alles Neue im Jahr 2026 – jetzt anmelden!")]
    [InlineData("Paket unterwegs", "Sendungsnummer 00340434161094042557, Ihr Code zur Abholung folgt.")]
    [InlineData("Code", "Beispiel: 123456 oder 000000")]
    public void IgnoresOtherNumbers(string subject, string text) =>
        Assert.Null(OneTimeCode.Find(subject, text));
}

public class SignalTests
{
    static MailSignals Read(string headers, string fromName = "Shop", string from = "news@shop.example", string to = "me@example.org")
    {
        var list = new HeaderList();
        foreach (var line in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            list.Add(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        return MailSignalReader.Read(list, fromName, from, to, ["me@example.org"]);
    }

    [Fact]
    public void Newsletter() => Assert.True(Read("List-Unsubscribe: <https://shop.example/u>").HasFlag(MailSignals.Newsletter));

    [Fact]
    public void AuthenticationFailure()
    {
        var s = Read("Authentication-Results: mx.example.org; spf=fail smtp.mailfrom=bank.example; dkim=none");
        Assert.True(s.HasFlag(MailSignals.AuthFailed));
        Assert.False(s.HasFlag(MailSignals.AuthPassed));
    }

    [Fact]
    public void ServerSpamAndSpoofedName()
    {
        var s = Read("X-Spam-Flag: YES", fromName: "service@paypal.com", from: "x@evil.example");
        Assert.True(s.HasFlag(MailSignals.ServerSpam));
        Assert.True(s.HasFlag(MailSignals.NameSpoof));
    }

    [Fact]
    public void SameOrganisationIsNoSpoof() =>
        Assert.False(Read("", fromName: "Shop.example Team", from: "info@mail.shop.example").HasFlag(MailSignals.NameSpoof));

    [Fact]
    public void ReplyToElsewhereAndDirect()
    {
        var s = Read("Reply-To: Hilfe <help@other.example>");
        Assert.True(s.HasFlag(MailSignals.ReplyToElsewhere));
        Assert.True(s.HasFlag(MailSignals.DirectlyAddressed));
    }
}

public class CategorizerTests
{
    static ClassifyInput Input(MailSignals signals, bool known = false, string subject = "Hallo", MailCategory? rule = null) => new(
        new Envelope { Subject = subject, FromName = "Anna", FromAddress = "anna@example.net", Preview = "Wie geht es dir?" },
        signals | MailSignals.Checked, MessageFlags.None, known, rule);

    [Fact]
    public void ContactIsPersonal() =>
        Assert.Equal(MailCategory.Personal, Categorizer.Classify(Input(MailSignals.DirectlyAddressed, known: true), null).Category);

    [Fact]
    public void NewsletterIsPromotion() =>
        Assert.Equal(MailCategory.Promotion, Categorizer.Classify(Input(MailSignals.Newsletter | MailSignals.DirectlyAddressed | MailSignals.AuthPassed), null).Category);

    [Fact]
    public void FailedChecksAreSuspect() =>
        Assert.Equal(MailCategory.SuspectedSpam, Categorizer.Classify(Input(MailSignals.ServerSpam | MailSignals.AuthFailed), null).Category);

    [Fact]
    public void SpamDetectionOffNeverSuspects() =>
        Assert.NotEqual(MailCategory.SuspectedSpam, Categorizer.Classify(Input(MailSignals.ServerSpam | MailSignals.AuthFailed), null, spamDetection: false).Category);

    [Fact]
    public void SenderRuleWins() =>
        Assert.Equal(MailCategory.Promotion, Categorizer.Classify(Input(MailSignals.DirectlyAddressed, known: true, rule: MailCategory.Promotion), null).Category);

    [Fact]
    public void PersonalMailFromStrangerIsPersonal() =>
        Assert.Equal(MailCategory.Personal, Categorizer.Classify(Input(MailSignals.DirectlyAddressed | MailSignals.AuthPassed), null).Category);

    [Theory]
    [InlineData("Ihre Rechnung Nr. 2026-1142")]
    [InlineData("Bestellbestätigung: Ihre Bestellung bei Beispiel-Shop")]
    [InlineData("Deine Bestellung wurde versendet")]
    [InlineData("Your order has shipped")]
    [InlineData("Ihre Jahresabrechnung 2026")]
    [InlineData("Zahlungsbestätigung")]
    public void ReceiptsAreInvoices(string subject) =>
        Assert.Equal(MailCategory.Invoice, Categorizer.Classify(Input(MailSignals.DirectlyAddressed | MailSignals.Automated, subject: subject), null).Category);

    [Fact]
    public void InvoiceEvenFromAShopNewsletterSender() =>
        Assert.Equal(MailCategory.Invoice, Categorizer.Classify(Input(MailSignals.Bulk | MailSignals.Newsletter, subject: "Ihre Rechnung zur Bestellung 4711"), null).Category);

    [Theory]
    [InlineData("10 % Rabatt auf deine nächste Bestellung")]
    [InlineData("Jetzt bestellen: Gratis-Versand nur heute")]
    public void AdvertisingAboutOrdersIsNoInvoice(string subject) =>
        Assert.Equal(MailCategory.Promotion, Categorizer.Classify(Input(MailSignals.Newsletter, subject: subject), null).Category);

    [Fact]
    public void FakeInvoiceIsSpamNotInvoice() =>
        Assert.Equal(MailCategory.SuspectedSpam, Categorizer.Classify(Input(MailSignals.ServerSpam | MailSignals.AuthFailed, subject: "Ihre Rechnung"), null).Category);

    [Fact]
    public void OrderNumberInTextIsInvoice() =>
        Assert.True(Categorizer.IsInvoice(new Envelope { Subject = "Danke für Ihren Einkauf", Preview = "Bestellnummer: 302-1234567 · Gesamtbetrag 23,90 €" }));

    [Fact]
    public void WordFilterLearns()
    {
        var samples = new List<(string, bool)>();
        for (var i = 0; i < 20; i++)
        {
            samples.Add(($"Gewinnen Sie jetzt Bitcoin Casino Bonus {i}", true));
            samples.Add(($"Protokoll der Teamsitzung Projekt Wisp {i}", false));
        }
        var filter = SpamFilter.Train(samples);
        Assert.True(filter.IsTrained);
        Assert.True(filter.Probability("Casino Bonus für Sie") > 0.8);
        Assert.True(filter.Probability("Teamsitzung morgen Projekt") < 0.2);
    }
}
