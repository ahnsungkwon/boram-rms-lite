using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text.RegularExpressions;
namespace BoramRms.Lite;
public static class PhoneResults
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static string Apply(FolderContext context, List<ImageItem> items)
    {
        try { return ReadResults(context, items); }
        catch (Exception ex) { return "전화번호 확인 보류 · " + ex.Message; }
    }
    private static string ReadResults(FolderContext context, List<ImageItem> items)
    {
        // Only explicitly named local result workbooks are inspected; no ERP lookup.
        var dirs = new[] { context.Root, context.ProcessedRoot, context.EventRoot }.Where(p => p != null).Distinct();
        var files = dirs.SelectMany(p => Directory.EnumerateFiles(p!, "전화번호_조회결과*.xlsx", SearchOption.TopDirectoryOnly))
            .Where(p => !p.Contains("열람용")).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        if (files.Count == 0) return "전화번호 확인: 로컬 결과 자료 없음";
        var rows = new List<(string Name, string Quota, string Phone)>();
        foreach (var file in files.Take(12))
        {
            SafePaths.NoLinks(file);
            if (new FileInfo(file).Length > 40 * 1024 * 1024) throw new IOException("결과 파일이 너무 큽니다.");
            using var zip = ZipFile.OpenRead(file);
            if (zip.Entries.Sum(e => e.Length) > 160 * 1024 * 1024) throw new IOException("결과 파일의 압축 해제 크기 제한을 초과합니다.");
            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") is { } strings)
            { using var input = strings.Open(); shared = XDocument.Load(input).Descendants(Ns + "si").Select(el => string.Concat(el.Descendants(Ns + "t").Select(t => t.Value))).ToList(); }
            foreach (var sheet in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml")))
            {
                using var stream = sheet.Open(); var doc = XDocument.Load(stream);
                bool headerFound = false; string? nameCol = null, quotaCol = null, phoneCol = null;
                foreach (var row in doc.Descendants(Ns + "row").Take(100000))
                {
                    var cells = row.Elements(Ns + "c").ToDictionary(c => Regex.Replace((string?)c.Attribute("r") ?? "", "[0-9]", ""), c =>
                    {
                        var value = c.Element(Ns + "v")?.Value ?? string.Concat(c.Descendants(Ns + "t").Select(t => t.Value));
                        return (string?)c.Attribute("t") == "s" && int.TryParse(value, out var n) && n >= 0 && n < shared.Count ? shared[n] : value;
                    });
                    if (!headerFound)
                    {
                        nameCol = cells.FirstOrDefault(c => new[] { "이름", "성명", "성함", "신청자", "회원명" }.Contains(c.Value.Replace(" ", ""))).Key;
                        phoneCol = cells.FirstOrDefault(c => c.Value.Contains("전화") || c.Value.Contains("휴대폰") || c.Value.Contains("연락처")).Key;
                        quotaCol = cells.FirstOrDefault(c => c.Value.Contains("구좌")).Key;
                        headerFound = nameCol != null && phoneCol != null;
                        continue;
                    }
                    var name = cells.GetValueOrDefault(nameCol!, ""); if (name.Length == 0) continue;
                    var phone = Regex.Replace(cells.GetValueOrDefault(phoneCol!, ""), "[^0-9]", "");
                    if (phone.Length == 10 && phone.StartsWith("10")) phone = "0" + phone;
                    rows.Add((TextDocument.Normal(name), quotaCol == null ? "" : Regex.Match(cells.GetValueOrDefault(quotaCol, ""), "[0-9]+").Value, phone));
                }
            }
        }
        if (rows.Count == 0) return "전화번호 확인: 결과 파일의 열 구조를 확인하지 못함";
        foreach (var item in items)
        {
            var matches = rows.Where(r => r.Name == TextDocument.Normal(item.Name) && (r.Quota.Length == 0 || r.Quota == item.Quota));
            var phones = matches.Select(r => r.Phone).Where(p => Regex.IsMatch(p, "^01[0-9]{8,9}$")).Distinct().ToList();
            item.PhoneIssue = phones.Count == 0 ? "전화번호 후보 없음 · 로컬 결과 기준" : phones.Count > 1 ? "전화번호 후보 여러 개 · 직접 확인 필요" : "";
        }
        return $"전화번호 확인 필요 {items.Count(i => i.PhoneIssue.Length > 0)}건 · 로컬 결과 기준";
    }
}
