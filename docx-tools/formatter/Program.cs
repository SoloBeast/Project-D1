using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
XNamespace pic = "http://schemas.microsoft.com/office/drawing/2012/picture";

if (args.Length < 2)
{
    Console.WriteLine("Usage: formatter <input.docx> <output.docx>");
    return 1;
}
string input = args[0], output = args[1];
File.Copy(input, output, true);

using var zip = ZipFile.Open(output, ZipArchiveMode.Update);
XDocument doc = LoadXml(zip, "word/document.xml");
XDocument styles = LoadXml(zip, "word/styles.xml");
XDocument settings = LoadXml(zip, "word/settings.xml");

var body = doc.Root!.Element(w + "body")!;

// ---------- 1. A4 section ----------
var sectPr = body.Element(w + "sectPr");
if (sectPr == null)
{
    var lastP = body.Elements(w + "p").LastOrDefault();
    sectPr = new XElement(w + "sectPr");
    (lastP ?? (XElement)body).Add(sectPr);
}
// A4: 11906 x 16838 twips (210x297mm). Margins 2cm = 1134 twips.
SetOrUpdate(sectPr, w + "pgSz",
    new XAttribute(w + "w", "11906"), new XAttribute(w + "h", "16838"));
SetOrUpdate(sectPr, w + "pgMar",
    new XAttribute(w + "top", "1134"), new XAttribute(w + "bottom", "1134"),
    new XAttribute(w + "left", "1134"), new XAttribute(w + "right", "1134"),
    new XAttribute(w + "header", "708"), new XAttribute(w + "footer", "708"),
    new XAttribute(w + "gutter", "0"));
var cols = sectPr.Element(w + "cols");
if (cols == null) sectPr.Add(new XElement(w + "cols", new XAttribute(w + "space", "720")));

// ---------- 2. settings: update fields ----------
{
    var sRoot = settings.Root!;
    var upd = sRoot.Element(w + "updateFields");
    if (upd == null) sRoot.Add(new XElement(w + "updateFields", new XAttribute(w + "val", "true")));
    else upd.SetAttributeValue(w + "val", "true");
    SaveXml(zip, "word/settings.xml", settings);
}

// ---------- 3. styles: readable print styles ----------
PatchStyles(styles);
SaveXml(zip, "word/styles.xml", styles);

// ---------- 4. collect block list ----------
var blocks = body.Elements().Where(e => e.Name == w + "p" || e.Name == w + "tbl" || e.Name == w + "sdt").ToList();
var paras = body.Elements(w + "p").ToList();
string ParaText(XElement p) => string.Concat(p.Descendants(w + "t").Select(t => (string?)t.Value ?? ""));
string ParaStyle(XElement p) => (string?)p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val") ?? "";

// Find task headings (Heading3 containing Task N)
int[] taskIdx = new int[5]; // block indices
for (int t = 1; t <= 5; t++)
{
    taskIdx[t - 1] = -1;
    for (int i = 0; i < blocks.Count; i++)
    {
        if (blocks[i].Name != w + "p") continue;
        var st = ParaStyle(blocks[i]);
        var tx = ParaText(blocks[i]);
        if (st == "Heading3" && Regex.IsMatch(tx, $@"Task\s*{t}\b", RegexOptions.IgnoreCase))
        { taskIdx[t - 1] = i; break; }
    }
}
// Recap heading
int recapIdx = -1;
for (int i = 0; i < blocks.Count; i++)
{
    if (blocks[i].Name != w + "p") continue;
    if (Regex.IsMatch(ParaText(blocks[i]), @"Topic Transition Recap", RegexOptions.IgnoreCase))
    { recapIdx = i; break; }
}
// How-does-it-work heading (Heading2)
int howIdx = -1;
for (int i = 0; i < blocks.Count; i++)
{
    if (blocks[i].Name != w + "p") continue;
    if (ParaStyle(blocks[i]) == "Heading2" && Regex.IsMatch(ParaText(blocks[i]), @"How does Meterpreter work", RegexOptions.IgnoreCase))
    { howIdx = i; break; }
}
// Q headings: NormalWeb (or any) paras starting with Qn:
int[] qIdx = new int[9];
for (int q = 1; q <= 9; q++)
{
    qIdx[q - 1] = -1;
    for (int i = 0; i < blocks.Count; i++)
    {
        if (blocks[i].Name != w + "p") continue;
        var tx = ParaText(blocks[i]);
        if (Regex.IsMatch(tx, $@"Q{q}\s*:", RegexOptions.IgnoreCase))
        { qIdx[q - 1] = i; break; }
    }
}
Console.WriteLine($"tasks: {string.Join(",", taskIdx)} recap:{recapIdx} how:{howIdx} q:{string.Join(",", qIdx)}");

// ---------- 5. Runs: red commands / blue links ----------
var cmdLike = new Regex(@"^\s*(>|&gt;|»)?\s*(msfconsole|msfvenom\b.*|use\s+\S.*|set\s+\S.*|show\s+(options|payloads).*|run\s*$|background$|back$|sessions\b.*|sysinfo$|getuid$|getpid$|ps\b.*|migrate\b.*|hashdump$|search\b.*|cat\b.*|load\s+\w+.*|getsystem$|keyscan_\w+.*|webcam_\w+.*|screenshot|screenshare|record_mic|clearev|execute\b.*|kill\b.*|pkill.*|reboot|shutdown|shell$|upload\b.*|download\b.*|ls$|cd\b.*|pwd$|edit\b.*|rm\b.*|arp$|ifconfig|netstat|portfwd|route\b.*|guid$|info$|irb$|enum_domain|enum_shares|psexec\b.*|crackstation.*|.*secrets\.txt.*|.*realsecret\.txt.*|.*migrate\s+\d+.*|.*lsass\.exe.*|ACME-TEST|FLASH|speedster|KDSvbsw3849!|Trustno1|Password1|ballen\s*$|10\.\d+\.\d+\.\d+.*|445\s*$|4444\s*$|exploit/windows/smb/psexec|post/windows/gather/\S+)\s*$",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
var darkFills = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "3E475A", "1C2539", "212C42" };

foreach (var p in body.Descendants(w + "p"))
{
    string pText = string.Concat(p.Descendants(w + "t").Select(t => (string?)t.Value ?? ""));
    bool stepsCtx = Regex.IsMatch(pText, @"Step|Q[1-9]\s*:|AttackBox|meterpreter\s*>|Run:|Enter:|prompt|SESSION|RHOSTS|LHOST|SMB", RegexOptions.IgnoreCase)
        || pText.Contains(">") || pText.Contains("»");
    bool inHyperlinkPara = p.Descendants(w + "hyperlink").Any();
    foreach (var run in p.Elements(w + "r").ToList())
    {
        string t = string.Concat(run.Elements(w + "t").Select(t => (string?)t.Value ?? ""));
        if (string.IsNullOrEmpty(t)) continue;
        var rPr = run.Element(w + "rPr");
        string rStyle = (string?)rPr?.Element(w + "rStyle")?.Attribute(w + "val") ?? "";
        string color = (string?)rPr?.Element(w + "color")?.Attribute(w + "val") ?? "";
        string fill = (string?)rPr?.Element(w + "shd")?.Attribute(w + "fill") ?? "";
        string asciiFont = (string?)rPr?.Element(w + "rFonts")?.Attribute(w + "ascii") ?? "";
        bool codeStyled = rStyle == "HTMLCode" || darkFills.Contains(fill) ||
            ((color == "FFFFFF" || color == "F9F9FB" || color == "B8BEC9") &&
             (asciiFont.Contains("Courier", StringComparison.OrdinalIgnoreCase) || asciiFont.Contains("Consolas", StringComparison.OrdinalIgnoreCase) || darkFills.Contains(fill)));
        bool inLink = run.Ancestors(w + "hyperlink").Any() || run.Ancestors(w + "ins").Any();
        // also check explicit hyperlink instr
        if (inLink || Regex.IsMatch(t, @"https?://", RegexOptions.IgnoreCase) && inHyperlinkPara)
        {
            ApplyBlueLink(run, w);
            continue;
        }
        if (Regex.IsMatch(t.Trim(), @"^https?://\S+$"))
        {
            ApplyBlueLink(run, w);
            continue;
        }
        string trimmed = t.Trim().TrimStart('>', '»', ' ').Trim();
        bool like = cmdLike.IsMatch(t) || cmdLike.IsMatch(trimmed);
        if (codeStyled || (like && (stepsCtx || t.Length < 60)))
        {
            // avoid recoloring table headers / generic words
            string low = t.Trim().ToLowerInvariant();
            if (low == "option" || low == "value" || low == "field" || low == "username:" || low == "password:") continue;
            // avoid single common words in prose unless codeStyled
            if (!codeStyled && (low == "help" || low == "search" || low == "shell" || low == "run" || low == "ps" || low == "ls")) continue;
            ApplyRedCode(run, w);
        }
    }
    // hyperlink elements: ensure blue
    foreach (var hl in p.Descendants(w + "hyperlink").ToList())
    {
        foreach (var run in hl.Descendants(w + "r"))
            ApplyBlueLink(run, w);
    }
}

// URLs in instrText HYPERLINK fields -> blue following runs (best effort: already handled)

// ---------- 6. Tables: keep on one page, readable ----------
foreach (var tbl in body.Descendants(w + "tbl").ToList())
{
    var tblPr = tbl.Element(w + "tblPr");
    if (tblPr == null) { tblPr = new XElement(w + "tblPr"); tbl.AddFirst(tblPr); }
    // width: 100% of text width
    var tblW = tblPr.Element(w + "tblW");
    if (tblW == null) tblPr.Add(new XElement(w + "tblW", new XAttribute(w + "w", "5000"), new XAttribute(w + "type", "pct")));
    else { tblW.SetAttributeValue(w + "w", "5000"); tblW.SetAttributeValue(w + "type", "pct"); }
    if (tblPr.Element(w + "tblLayout") == null) tblPr.Add(new XElement(w + "tblLayout", new XAttribute(w + "type", "fixed")));
    if (tblPr.Element(w + "jc") == null) tblPr.Add(new XElement(w + "jc", new XAttribute(w + "val", "center")));
    else tblPr.Element(w + "jc")!.SetAttributeValue(w + "val", "center");
    // borders
    var borders = tblPr.Element(w + "tblBorders");
    if (borders == null)
    {
        borders = new XElement(w + "tblBorders",
            new XElement(w + "top", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
            new XElement(w + "left", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
            new XElement(w + "bottom", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
            new XElement(w + "right", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
            new XElement(w + "insideH", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
            new XElement(w + "insideV", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")));
        tblPr.Add(borders);
    }
    // keep together: cantSplit on every row + tblOverlap never + keepNext handled by cantSplit
    var overlap = tblPr.Element(w + "tblOverlap");
    if (overlap == null) tblPr.Add(new XElement(w + "tblOverlap", new XAttribute(w + "val", "never")));
    var rows = tbl.Elements(w + "tr").ToList();
    for (int ri = 0; ri < rows.Count; ri++)
    {
        var tr = rows[ri];
        var trPr = tr.Element(w + "trPr");
        if (trPr == null) { trPr = new XElement(w + "trPr"); tr.AddFirst(trPr); }
        if (trPr.Element(w + "cantSplit") == null) trPr.Add(new XElement(w + "cantSplit", new XAttribute(w + "val", "true")));
        else trPr.Element(w + "cantSplit")!.SetAttributeValue(w + "val", "true");
        if (ri == 0 && trPr.Element(w + "tblHeader") == null) trPr.Add(new XElement(w + "tblHeader", new XAttribute(w + "val", "true")));
        foreach (var tc in tr.Elements(w + "tc"))
        {
            var tcPr = tc.Element(w + "tcPr");
            if (tcPr == null) { tcPr = new XElement(w + "tcPr"); tc.AddFirst(tcPr); }
            if (tcPr.Element(w + "vAlign") == null) tcPr.Add(new XElement(w + "vAlign", new XAttribute(w + "val", "center")));
            // header shading
            if (ri == 0)
            {
                var shd = tcPr.Element(w + "shd");
                if (shd == null) tcPr.Add(new XElement(w + "shd", new XAttribute(w + "val", "clear"), new XAttribute(w + "color", "auto"), new XAttribute(w + "fill", "1F4E79")));
                else { shd.SetAttributeValue(w + "val", "clear"); shd.SetAttributeValue(w + "color", "auto"); shd.SetAttributeValue(w + "fill", "1F4E79"); }
                foreach (var pr in tc.Descendants(w + "rPr"))
                {
                    var c = pr.Element(w + "color");
                    if (c == null) pr.Add(new XElement(w + "color", new XAttribute(w + "val", "FFFFFF")));
                    else c.SetAttributeValue(w + "val", "FFFFFF");
                    var b = pr.Element(w + "b");
                    if (b == null) pr.Add(new XElement(w + "b"));
                    var sz = pr.Element(w + "sz");
                    if (sz == null) pr.Add(new XElement(w + "sz", new XAttribute(w + "val", "22")));
                }
            }
            else
            {
                // ensure body cell text is black and readable, keep red code runs as-is
                foreach (var para in tc.Elements(w + "p"))
                {
                    var pPr = para.Element(w + "pPr");
                    if (pPr == null) { pPr = new XElement(w + "pPr"); para.AddFirst(pPr); }
                    var sz = pPr.Element(w + "sz");
                }
            }
            // cell margins via tcMar
            if (tcPr.Element(w + "tcMar") == null)
                tcPr.Add(new XElement(w + "tcMar",
                    new XElement(w + "top", new XAttribute(w + "w", "40"), new XAttribute(w + "type", "dxa")),
                    new XElement(w + "bottom", new XAttribute(w + "w", "40"), new XAttribute(w + "type", "dxa")),
                    new XElement(w + "left", new XAttribute(w + "w", "100"), new XAttribute(w + "type", "dxa")),
                    new XElement(w + "right", new XAttribute(w + "w", "100"), new XAttribute(w + "type", "dxa"))));
        }
    }
    // grid: 2 cols 30/70 if 2-col table
    var grid = tbl.Element(w + "tblGrid");
    int cols2 = rows.FirstOrDefault()?.Elements(w + "tc").Count() ?? 0;
    if (grid != null && cols2 == 2)
    {
        grid.RemoveAll();
        grid.Add(new XElement(w + "gridCol", new XAttribute(w + "w", "2700")));
        grid.Add(new XElement(w + "gridCol", new XAttribute(w + "w", "6300")));
        foreach (var tr in rows)
        {
            var tcs = tr.Elements(w + "tc").ToList();
            for (int ci = 0; ci < tcs.Count; ci++)
            {
                var tcPr = tcs[ci].Element(w + "tcPr");
                var tcW = tcPr?.Element(w + "tcW");
                if (tcW == null) tcPr?.Add(new XElement(w + "tcW", new XAttribute(w + "w", ci == 0 ? "2700" : "6300"), new XAttribute(w + "type", "dxa")));
                else { tcW.SetAttributeValue(w + "w", ci == 0 ? "2700" : "6300"); tcW.SetAttributeValue(w + "type", "dxa"); }
            }
        }
    }
}

// ---------- 7. Images: cap width, center ----------
const long MAX_CX = 5486400; // ~15.2 cm, fits A4 with 2cm margins
foreach (var drawing in body.Descendants(w + "drawing").ToList())
{
    foreach (var ext in drawing.Descendants().Where(e => e.Name.LocalName == "extent" || e.Name.LocalName == "ext"))
    {
        var cxA = ext.Attribute("cx"); var cyA = ext.Attribute("cy");
        if (cxA == null || cyA == null) continue;
        if (long.TryParse(cxA.Value, out long cx) && long.TryParse(cyA.Value, out long cy) && cx > MAX_CX && cx > 0 && cy > 0)
        {
            double k = (double)MAX_CX / cx;
            ext.SetAttributeValue("cx", MAX_CX.ToString());
            ext.SetAttributeValue("cy", ((long)(cy * k)).ToString());
        }
    }
    var par = drawing.Ancestors(w + "p").FirstOrDefault();
    if (par != null)
    {
        string pt = string.Concat(par.Descendants(w + "t").Select(t => (string?)t.Value ?? "")).Trim();
        if (pt.Length < 5)
        {
            var pPr = par.Element(w + "pPr");
            if (pPr == null) { pPr = new XElement(w + "pPr"); par.AddFirst(pPr); }
            var jc = pPr.Element(w + "jc");
            if (jc == null) pPr.Add(new XElement(w + "jc", new XAttribute(w + "val", "center")));
            else jc.SetAttributeValue(w + "val", "center");
            // keep image with next paragraph (caption/related text)
            if (pPr.Element(w + "keepNext") == null) pPr.Add(new XElement(w + "keepNext"));
            if (pPr.Element(w + "keepLines") == null) pPr.Add(new XElement(w + "keepLines"));
        }
    }
}

// ---------- 8. Page breaks before tasks + bookmarks ----------
// refresh block list after modifications above (blocks unchanged structurally)
blocks = body.Elements().Where(e => e.Name == w + "p" || e.Name == w + "tbl" || e.Name == w + "sdt").ToList();
// map para -> block index
var paraToBlock = new Dictionary<XElement, int>();
foreach (var b in blocks)
    foreach (var p in b.DescendantsAndSelf(w + "p"))
        if (!paraToBlock.ContainsKey(p)) paraToBlock[p] = blocks.IndexOf(b);

void SetPageBreakBefore(XElement p)
{
    var pPr = p.Element(w + "pPr");
    if (pPr == null) { pPr = new XElement(w + "pPr"); p.AddFirst(pPr); }
    if (pPr.Element(w + "pageBreakBefore") == null)
        pPr.Add(new XElement(w + "pageBreakBefore"));
}

int bmId = 100;
void AddBookmarkPair(XElement startPara, XElement endPara, string nameStart, string nameEnd)
{
    int idS = bmId++; int idE = bmId++;
    var bs = new XElement(w + "bookmarkStart", new XAttribute(w + "id", idS.ToString()), new XAttribute(w + "name", nameStart));
    var beS = new XElement(w + "bookmarkEnd", new XAttribute(w + "id", idS.ToString()));
    InsertMarker(startPara, bs, true); InsertMarker(startPara, beS, false);
    if (endPara == startPara)
    {
        var bs2 = new XElement(w + "bookmarkStart", new XAttribute(w + "id", idE.ToString()), new XAttribute(w + "name", nameEnd));
        var be2 = new XElement(w + "bookmarkEnd", new XAttribute(w + "id", idE.ToString()));
        InsertMarker(endPara, bs2, true); InsertMarker(endPara, be2, false);
    }
    else
    {
        var bs2 = new XElement(w + "bookmarkStart", new XAttribute(w + "id", idE.ToString()), new XAttribute(w + "name", nameEnd));
        var be2 = new XElement(w + "bookmarkEnd", new XAttribute(w + "id", idE.ToString()));
        InsertMarker(endPara, bs2, true); InsertMarker(endPara, be2, false);
    }
}
void InsertMarker(XElement para, XElement marker, bool atStart)
{
    var pPr = para.Element(w + "pPr");
    if (atStart)
    {
        if (pPr != null) pPr.AddAfterSelf(marker);
        else para.AddFirst(marker);
    }
    else para.Add(marker);
}
XElement? LastParaInBlockRange(int fromBlock, int toBlock)
{
    for (int i = toBlock; i >= fromBlock; i--)
    {
        var b = blocks[i];
        if (b.Name == w + "p") return b;
        var lastP = b.Descendants(w + "p").LastOrDefault();
        if (lastP != null) return lastP;
    }
    return null;
}

// set page breaks
foreach (var ti in taskIdx) if (ti >= 0) SetPageBreakBefore((XElement)blocks[ti]);
if (recapIdx >= 0) SetPageBreakBefore((XElement)blocks[recapIdx]);

// bookmark sections
var bm = new List<(string label, string start, string end)>();
if (taskIdx[0] >= 0 && taskIdx[1] > taskIdx[0])
{
    var s = (XElement)blocks[taskIdx[0]];
    var e = LastParaInBlockRange(taskIdx[0] + 1, taskIdx[1] - 1) ?? s;
    AddBookmarkPair(s, e, "Task1Start", "Task1End");
    bm.Add(("t1", "Task1Start", "Task1End"));
    if (howIdx > taskIdx[0] && howIdx < taskIdx[1])
    {
        var hs = (XElement)blocks[howIdx];
        AddBookmarkPair(hs, e, "HowStart", "HowEnd");
    }
}
if (taskIdx[1] >= 0 && taskIdx[2] > taskIdx[1])
{
    var s = (XElement)blocks[taskIdx[1]];
    var e = LastParaInBlockRange(taskIdx[1] + 1, taskIdx[2] - 1) ?? s;
    AddBookmarkPair(s, e, "Task2Start", "Task2End");
    bm.Add(("t2", "Task2Start", "Task2End"));
}
if (taskIdx[2] >= 0 && taskIdx[3] > taskIdx[2])
{
    var s = (XElement)blocks[taskIdx[2]];
    var e = LastParaInBlockRange(taskIdx[2] + 1, taskIdx[3] - 1) ?? s;
    AddBookmarkPair(s, e, "Task3Start", "Task3End");
    bm.Add(("t3", "Task3Start", "Task3End"));
}
if (taskIdx[3] >= 0 && taskIdx[4] > taskIdx[3])
{
    var s = (XElement)blocks[taskIdx[3]];
    var e = LastParaInBlockRange(taskIdx[3] + 1, taskIdx[4] - 1) ?? s;
    AddBookmarkPair(s, e, "Task4Start", "Task4End");
    bm.Add(("t4", "Task4Start", "Task4End"));
}
// Task5 overview: heading to before Q1
if (taskIdx[4] >= 0)
{
    int t5 = taskIdx[4];
    int t5EndBlock = (recapIdx > t5 ? recapIdx - 1 : blocks.Count - 1);
    var s = (XElement)blocks[t5];
    XElement overEnd;
    if (qIdx[0] > t5) overEnd = LastParaInBlockRange(t5 + 1, qIdx[0] - 1) ?? s;
    else overEnd = LastParaInBlockRange(t5 + 1, t5EndBlock) ?? s;
    AddBookmarkPair(s, overEnd, "Task5Start", "Task5OverEnd");
    bm.Add(("t5", "Task5Start", "Task5OverEnd"));
    // Q sections
    for (int q = 0; q < 9; q++)
    {
        if (qIdx[q] < 0) continue;
        int qs = qIdx[q];
        int qeBlock = blocks.Count - 1;
        // next Q or recap
        int next = -1;
        for (int k = q + 1; k < 9; k++) if (qIdx[k] > qs) { next = qIdx[k]; break; }
        if (next < 0 && recapIdx > qs) next = recapIdx;
        if (next < 0) qeBlock = blocks.Count - 1;
        else qeBlock = next - 1;
        var qsp = (XElement)blocks[qs];
        var qep = LastParaInBlockRange(qs, qeBlock) ?? qsp;
        AddBookmarkPair(qsp, qep, $"Q{q + 1}Start", $"Q{q + 1}End");
    }
}
if (recapIdx >= 0)
{
    var s = (XElement)blocks[recapIdx];
    var e = LastParaInBlockRange(recapIdx, blocks.Count - 1) ?? s;
    AddBookmarkPair(s, e, "RecapStart", "RecapEnd");
}

// ---------- 9. Title page + Index (insert at top) ----------
var titleElems = BuildTitlePage(w);
var indexElems = BuildIndex(w);
var firstBlock = body.Elements().FirstOrDefault();
foreach (var el in ((IEnumerable<XElement>)titleElems).Concat(indexElems).Reverse())
    firstBlock?.AddBeforeSelf(el);

// ---------- 10. Footer with page numbers ----------
EnsureFooter(zip, doc, w, r);

// ---------- save ----------
SaveXml(zip, "word/document.xml", doc);
Console.WriteLine("Saved formatted document.");
return 0;

// ================= helpers =================
static XDocument LoadXml(ZipArchive zip, string name)
{
    var e = zip.GetEntry(name)!;
    using var s = e.Open();
    return XDocument.Load(s, LoadOptions.PreserveWhitespace);
}
static void SaveXml(ZipArchive zip, string name, XDocument d)
{
    var e = zip.GetEntry(name)!;
    using var s = e.Open();
    s.SetLength(0);
    d.Save(s);
}
static void SetOrUpdate(XElement parent, XName name, params XAttribute[] attrs)
{
    var el = parent.Element(name);
    if (el == null) { el = new XElement(name); parent.Add(el); }
    foreach (var a in attrs) el.SetAttributeValue(a.Name, a.Value);
}
static void PatchStyles(XDocument styles)
{
    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    foreach (var st in styles.Root!.Elements(w + "style"))
    {
        string id = (string?)st.Attribute(w + "styleId") ?? "";
        var rPr = st.Element(w + "rPr");
        if (id == "Normal")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            SetRunFont(rPr, w, "Calibri", "22");
            var c = rPr.Element(w + "color");
            if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "1A1A1A")));
            else c.SetAttributeValue(w + "val", "1A1A1A");
        }
        if (id == "Heading1")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            SetRunFont(rPr, w, "Calibri", "40");
            var c = rPr.Element(w + "color");
            if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "1F4E79")));
            else c.SetAttributeValue(w + "val", "1F4E79");
            if (rPr.Element(w + "b") == null) rPr.Add(new XElement(w + "b"));
        }
        if (id == "Heading2")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            SetRunFont(rPr, w, "Calibri", "30");
            var c = rPr.Element(w + "color");
            if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "2E75B6")));
            else c.SetAttributeValue(w + "val", "2E75B6");
            if (rPr.Element(w + "b") == null) rPr.Add(new XElement(w + "b"));
        }
        if (id == "Heading3")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            SetRunFont(rPr, w, "Calibri", "26");
            var c = rPr.Element(w + "color");
            if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "1F4E79")));
            else c.SetAttributeValue(w + "val", "1F4E79");
            if (rPr.Element(w + "b") == null) rPr.Add(new XElement(w + "b"));
        }
        if (id == "Hyperlink")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            var c = rPr.Element(w + "color");
            if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "0000FF")));
            else c.SetAttributeValue(w + "val", "0000FF");
            if (rPr.Element(w + "u") == null) rPr.Add(new XElement(w + "u", new XAttribute(w + "val", "single")));
            else rPr.Element(w + "u")!.SetAttributeValue(w + "val", "single");
        }
        if (id == "NormalWeb")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            SetRunFont(rPr, w, "Calibri", "22");
        }
        if (id == "HTMLCode")
        {
            if (rPr == null) { rPr = new XElement(w + "rPr"); st.Add(rPr); }
            // print-friendly: red monospace on light grey
            SetRunFont(rPr, w, "Consolas", "20");
            var c = rPr.Element(w + "color");
            if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "FF0000")));
            else c.SetAttributeValue(w + "val", "FF0000");
            var shd = rPr.Element(w + "shd");
            if (shd == null) rPr.Add(new XElement(w + "shd", new XAttribute(w + "val", "clear"), new XAttribute(w + "color", "auto"), new XAttribute(w + "fill", "F2F2F2")));
            else { shd.SetAttributeValue(w + "val", "clear"); shd.SetAttributeValue(w + "fill", "F2F2F2"); }
        }
    }
}
static void SetRunFont(XElement rPr, XNamespace w, string font, string szHalfPts)
{
    var rf = rPr.Element(w + "rFonts");
    if (rf == null) { rf = new XElement(w + "rFonts"); rPr.Add(rf); }
    rf.SetAttributeValue(w + "ascii", font);
    rf.SetAttributeValue(w + "hAnsi", font);
    rf.SetAttributeValue(w + "cs", font);
    var sz = rPr.Element(w + "sz");
    if (sz == null) rPr.Add(new XElement(w + "sz", new XAttribute(w + "val", szHalfPts)));
    else sz.SetAttributeValue(w + "val", szHalfPts);
    var szCs = rPr.Element(w + "szCs");
    if (szCs == null) rPr.Add(new XElement(w + "szCs", new XAttribute(w + "val", szHalfPts)));
    else szCs.SetAttributeValue(w + "val", szHalfPts);
}
static void ApplyRedCode(XElement run, XNamespace w)
{
    var rPr = run.Element(w + "rPr");
    if (rPr == null) { rPr = new XElement(w + "rPr"); run.AddFirst(rPr); }
    var c = rPr.Element(w + "color");
    if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "FF0000")));
    else c.SetAttributeValue(w + "val", "FF0000");
    var rf = rPr.Element(w + "rFonts");
    if (rf == null) { rf = new XElement(w + "rFonts"); rPr.Add(rf); }
    rf.SetAttributeValue(w + "ascii", "Consolas");
    rf.SetAttributeValue(w + "hAnsi", "Consolas");
    rf.SetAttributeValue(w + "cs", "Consolas");
    var sz = rPr.Element(w + "sz");
    if (sz == null) rPr.Add(new XElement(w + "sz", new XAttribute(w + "val", "20")));
    else sz.SetAttributeValue(w + "val", "20");
    var szCs = rPr.Element(w + "szCs");
    if (szCs == null) rPr.Add(new XElement(w + "szCs", new XAttribute(w + "val", "20")));
    else szCs.SetAttributeValue(w + "val", "20");
    var shd = rPr.Element(w + "shd");
    if (shd == null) rPr.Add(new XElement(w + "shd", new XAttribute(w + "val", "clear"), new XAttribute(w + "color", "auto"), new XAttribute(w + "fill", "F2F2F2")));
    else { shd.SetAttributeValue(w + "val", "clear"); shd.SetAttributeValue(w + "color", "auto"); shd.SetAttributeValue(w + "fill", "F2F2F2"); }
}
static void ApplyBlueLink(XElement run, XNamespace w)
{
    var rPr = run.Element(w + "rPr");
    if (rPr == null) { rPr = new XElement(w + "rPr"); run.AddFirst(rPr); }
    var c = rPr.Element(w + "color");
    if (c == null) rPr.Add(new XElement(w + "color", new XAttribute(w + "val", "0000FF")));
    else c.SetAttributeValue(w + "val", "0000FF");
    var u = rPr.Element(w + "u");
    if (u == null) rPr.Add(new XElement(w + "u", new XAttribute(w + "val", "single")));
    else u.SetAttributeValue(w + "val", "single");
    // remove dark shading that would hide blue link
    var shd = rPr.Element(w + "shd");
    if (shd != null)
    {
        string? fill = (string?)shd.Attribute(w + "fill");
        if (fill == "3E475A" || fill == "1C2539" || fill == "212C42")
            shd.SetAttributeValue(w + "fill", "auto");
    }
}
static List<XElement> BuildTitlePage(XNamespace w)
{
    XElement P(string? style, string align, string size, string color, bool bold, bool italic, bool caps, string text, int spaceAfter = 120, bool pageBreak = false)
    {
        var p = new XElement(w + "p",
            new XElement(w + "pPr",
                style != null ? new XElement(w + "pStyle", new XAttribute(w + "val", style)) : null,
                new XElement(w + "jc", new XAttribute(w + "val", align)),
                new XElement(w + "spacing", new XAttribute(w + "after", spaceAfter.ToString()), new XAttribute(w + "line", "276"), new XAttribute(w + "lineRule", "auto"))));
        var rPr = new XElement(w + "rPr",
            new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
            new XElement(w + "sz", new XAttribute(w + "val", size)),
            new XElement(w + "szCs", new XAttribute(w + "val", size)),
            new XElement(w + "color", new XAttribute(w + "val", color)));
        if (bold) rPr.Add(new XElement(w + "b"));
        if (italic) rPr.Add(new XElement(w + "i"));
        if (caps) rPr.Add(new XElement(w + "caps", new XAttribute(w + "val", "true")));
        p.Add(new XElement(w + "r", rPr, new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
        if (pageBreak) p.Add(new XElement(w + "r", new XElement(w + "br", new XAttribute(w + "type", "page"))));
        return p;
    }
    return new List<XElement>
    {
        new XElement(w + "p", new XElement(w + "pPr", new XElement(w + "spacing", new XAttribute(w + "before", "2880")))),
        P("Title", "center", "56", "1F4E79", true, false, false, "Metasploit: Meterpreter", 200),
        P(null, "center", "28", "2E75B6", true, false, false, "Cyber Security 101  ·  Exploitation Basics", 200),
        P(null, "center", "22", "404040", false, true, false, "Take a deep dive into Meterpreter, and see how in-memory payloads can be used for post-exploitation.", 300),
        P(null, "center", "22", "404040", false, false, false, "Duration: 20 min  ·  Print-Ready A4 Edition", 600),
        P(null, "center", "18", "808080", false, false, false, DateTime.Now.ToString("dd MMMM yyyy"), 400),
        P(null, "center", "18", "808080", false, true, false, "Formatted from the supplied source document. Wording, commands, answers and technical details are unchanged; only layout, pagination and print formatting were applied.", 0, true),
    };
}
static List<XElement> BuildIndex(XNamespace w)
{
    var elems = new List<XElement>();
    elems.Add(new XElement(w + "p",
        new XElement(w + "pPr",
            new XElement(w + "pStyle", new XAttribute(w + "val", "Heading1")),
            new XElement(w + "jc", new XAttribute(w + "val", "left")),
            new XElement(w + "spacing", new XAttribute(w + "after", "120"))),
        new XElement(w + "r",
            new XElement(w + "rPr",
                new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
                new XElement(w + "sz", new XAttribute(w + "val", "40")),
                new XElement(w + "szCs", new XAttribute(w + "val", "40")),
                new XElement(w + "color", new XAttribute(w + "val", "1F4E79")),
                new XElement(w + "b")),
            new XElement(w + "t", "Index"))));
    elems.Add(new XElement(w + "p",
        new XElement(w + "pPr", new XElement(w + "spacing", new XAttribute(w + "after", "200"))),
        new XElement(w + "r",
            new XElement(w + "rPr",
                new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
                new XElement(w + "sz", new XAttribute(w + "val", "20")),
                new XElement(w + "szCs", new XAttribute(w + "val", "20")),
                new XElement(w + "color", new XAttribute(w + "val", "595959")),
                new XElement(w + "i")),
            new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), "Page numbers are live fields. In Word, press Ctrl+A then F9 and choose “Update entire table” to refresh them after opening, then print to A4."))));
    var rows = new List<(string title, string s, string e, bool range)>
    {
        ("Task 1 — Introduction to Meterpreter (incl. How does Meterpreter work?)", "Task1Start", "Task1End", true),
        ("How does Meterpreter work? (key concepts)", "HowStart", "HowEnd", true),
        ("Task 2 — Meterpreter Flavors", "Task2Start", "Task2End", true),
        ("Task 3 — Meterpreter Commands (Core · File system · Networking · System · Other)", "Task3Start", "Task3End", true),
        ("Task 4 — Post-Exploitation with Meterpreter (Help · getuid/ps · Migrate · Hashdump · Search · Shell)", "Task4Start", "Task4End", true),
        ("Task 5 — Post-Exploitation Challenge (overview, SMB credentials, Kiwi/load)", "Task5Start", "Task5OverEnd", true),
        ("Q1 — Computer name (sysinfo)", "Q1Start", "Q1End", true),
        ("Q2 — Target domain (post/windows/gather/enum_domain)", "Q2Start", "Q2End", true),
        ("Q3 — User-created share (post/windows/gather/enum_shares)", "Q3Start", "Q3End", true),
        ("Q4 — NTLM hash of jchambers (migrate to lsass.exe, hashdump)", "Q4Start", "Q4End", true),
        ("Q5 — Cleartext password of jchambers (online hash lookup)", "Q5Start", "Q5End", true),
        ("Q6 — Location of secrets.txt (search -f)", "Q6Start", "Q6End", true),
        ("Q7 — Twitter password in secrets.txt (cat)", "Q7Start", "Q7End", true),
        ("Q8 — Location of realsecret.txt (search -f)", "Q8Start", "Q8End", true),
        ("Q9 — The real secret (cat realsecret.txt)", "Q9Start", "Q9End", true),
        ("Topic Transition Recap", "RecapStart", "RecapEnd", true),
    };
    var tbl = new XElement(w + "tbl",
        new XElement(w + "tblPr",
            new XElement(w + "tblW", new XAttribute(w + "w", "5000"), new XAttribute(w + "type", "pct")),
            new XElement(w + "tblLayout", new XAttribute(w + "type", "fixed")),
            new XElement(w + "jc", new XAttribute(w + "val", "center")),
            new XElement(w + "tblBorders",
                new XElement(w + "top", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
                new XElement(w + "left", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
                new XElement(w + "bottom", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
                new XElement(w + "right", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
                new XElement(w + "insideH", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE")),
                new XElement(w + "insideV", new XAttribute(w + "val", "single"), new XAttribute(w + "sz", "6"), new XAttribute(w + "color", "B4C2DE"))),
            new XElement(w + "tblOverlap", new XAttribute(w + "val", "never"))),
        new XElement(w + "tblGrid",
            new XElement(w + "gridCol", new XAttribute(w + "w", "6750")),
            new XElement(w + "gridCol", new XAttribute(w + "w", "2250"))));
    // header
    tbl.Add(IndexRow(w, "Section", "Pages", true, null, null));
    foreach (var (title, s, e, range) in rows)
        tbl.Add(IndexRow(w, title, range ? "Pages" : "Page", false, s, e));
    elems.Add(tbl);
    // page break after index
    elems.Add(new XElement(w + "p",
        new XElement(w + "r", new XElement(w + "br", new XAttribute(w + "type", "page")))));
    return elems;
}
static XElement IndexRow(XNamespace w, string title, string pagesLabel, bool header, string? bmStart, string? bmEnd)
{
    XElement Cell(string? text, bool isHeader, bool isPages, string? bs, string? be)
    {
        var tc = new XElement(w + "tc",
            new XElement(w + "tcPr",
                new XElement(w + "tcW", new XAttribute(w + "w", isPages ? "2250" : "6750"), new XAttribute(w + "type", "dxa")),
                new XElement(w + "vAlign", new XAttribute(w + "val", "center")),
                isHeader
                    ? new XElement(w + "shd", new XAttribute(w + "val", "clear"), new XAttribute(w + "color", "auto"), new XAttribute(w + "fill", "1F4E79"))
                    : new XElement(w + "shd", new XAttribute(w + "val", "clear"), new XAttribute(w + "color", "auto"), new XAttribute(w + "fill", "auto"))),
            new XElement(w + "p",
                new XElement(w + "pPr", new XElement(w + "spacing", new XAttribute(w + "before", "40"), new XAttribute(w + "after", "40"))),
                BuildCellRuns(w, text, isHeader, isPages, bs, be)));
        return tc;
    }
    var tr = new XElement(w + "tr",
        new XElement(w + "trPr",
            new XElement(w + "cantSplit", new XAttribute(w + "val", "true")),
            header ? new XElement(w + "tblHeader", new XAttribute(w + "val", "true")) : null),
        Cell(title, header, false, null, null),
        Cell(null, header, true, bmStart, bmEnd));
    // fix pagesLabel header text
    if (header)
    {
        var pagesCell = tr.Elements(w + "tc").Skip(1).First();
        var p = pagesCell.Element(w + "p")!;
        p.RemoveNodes();
        p.Add(new XElement(w + "pPr", new XElement(w + "jc", new XAttribute(w + "val", "center")), new XElement(w + "spacing", new XAttribute(w + "before", "40"), new XAttribute(w + "after", "40"))),
            new XElement(w + "r", HeaderRPr(w), new XElement(w + "t", "Pages")));
    }
    return tr;
}
static XElement HeaderRPr(XNamespace w) => new XElement(w + "rPr",
    new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
    new XElement(w + "sz", new XAttribute(w + "val", "22")),
    new XElement(w + "szCs", new XAttribute(w + "val", "22")),
    new XElement(w + "color", new XAttribute(w + "val", "FFFFFF")),
    new XElement(w + "b"));
static IEnumerable<XElement> BuildCellRuns(XNamespace w, string? text, bool isHeader, bool isPages, string? bs, string? be)
{
    if (isHeader)
    {
        yield return new XElement(w + "r", HeaderRPr(w), new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text ?? ""));
        yield break;
    }
    if (!isPages)
    {
        var rPr = new XElement(w + "rPr",
            new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
            new XElement(w + "sz", new XAttribute(w + "val", "20")),
            new XElement(w + "szCs", new XAttribute(w + "val", "20")),
            new XElement(w + "color", new XAttribute(w + "val", "1A1A1A")));
        yield return new XElement(w + "r", rPr, new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text ?? ""));
        yield break;
    }
    // pages cell: "Pages {PAGEREF start}–{PAGEREF end}"
    var pr = new XElement(w + "rPr",
        new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
        new XElement(w + "sz", new XAttribute(w + "val", "20")),
        new XElement(w + "szCs", new XAttribute(w + "val", "20")),
        new XElement(w + "color", new XAttribute(w + "val", "1A1A1A")));
    yield return new XElement(w + "r", pr, new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), "Pages "));
    foreach (var r in PageRef(w, bs!)) yield return r;
    yield return new XElement(w + "r", pr, new XElement(w + "t", "–"));
    foreach (var r in PageRef(w, be!)) yield return r;
}
static IEnumerable<XElement> PageRef(XNamespace w, string bookmark)
{
    yield return new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "begin")));
    yield return new XElement(w + "r", new XElement(w + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), $" PAGEREF {bookmark} \\h "));
    yield return new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "separate")));
    yield return new XElement(w + "r", new XElement(w + "t", "–"));
    yield return new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "end")));
}
static void EnsureFooter(ZipArchive zip, XDocument doc, XNamespace w, XNamespace r)
{
    // create footer1.xml
    const string footerName = "word/footer1.xml";
    var footer = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(w + "ftr",
            new XAttribute(XNamespace.Xmlns + "w", w.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", r.NamespaceName),
            new XElement(w + "p",
                new XElement(w + "pPr",
                    new XElement(w + "jc", new XAttribute(w + "val", "center")),
                    new XElement(w + "spacing", new XAttribute(w + "before", "120"))),
                new XElement(w + "r",
                    new XElement(w + "rPr",
                        new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
                        new XElement(w + "sz", new XAttribute(w + "val", "18")),
                        new XElement(w + "szCs", new XAttribute(w + "val", "18")),
                        new XElement(w + "color", new XAttribute(w + "val", "808080"))),
                    new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), "Metasploit: Meterpreter  ·  Page ")),
                new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "begin"))),
                new XElement(w + "r", new XElement(w + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), " PAGE ")),
                new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "separate"))),
                new XElement(w + "r", new XElement(w + "rPr",
                        new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
                        new XElement(w + "sz", new XAttribute(w + "val", "18")),
                        new XElement(w + "color", new XAttribute(w + "val", "808080"))),
                    new XElement(w + "t", "1")),
                new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "end"))),
                new XElement(w + "r",
                    new XElement(w + "rPr",
                        new XElement(w + "rFonts", new XAttribute(w + "ascii", "Calibri"), new XAttribute(w + "hAnsi", "Calibri")),
                        new XElement(w + "sz", new XAttribute(w + "val", "18")),
                        new XElement(w + "color", new XAttribute(w + "val", "808080"))),
                    new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), " of ")),
                new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "begin"))),
                new XElement(w + "r", new XElement(w + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), " NUMPAGES ")),
                new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "separate"))),
                new XElement(w + "r", new XElement(w + "t", "1")),
                new XElement(w + "r", new XElement(w + "fldChar", new XAttribute(w + "fldCharType", "end"))))));
    var existing = zip.GetEntry(footerName);
    existing?.Delete();
    var fe = zip.CreateEntry(footerName);
    using (var s = fe.Open()) footer.Save(s);
    // content types
    var ctEntry = zip.GetEntry("[Content_Types].xml")!;
    XDocument ct;
    using (var s = ctEntry.Open()) ct = XDocument.Load(s);
    XNamespace ctn = "http://schemas.openxmlformats.org/package/2006/content-types";
    if (!ct.Root!.Elements(ctn + "Override").Any(e => (string?)e.Attribute("PartName") == "/word/footer1.xml"))
        ct.Root.Add(new XElement(ctn + "Override", new XAttribute("PartName", "/word/footer1.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml")));
    using (var s = ctEntry.Open()) { s.SetLength(0); ct.Save(s); }
    // rels: new rId
    var relsEntry = zip.GetEntry("word/_rels/document.xml.rels")!;
    XDocument rels;
    using (var s = relsEntry.Open()) rels = XDocument.Load(s);
    XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    int maxId = 0;
    foreach (var re in rels.Root!.Elements(rel + "Relationship"))
    {
        var id = (string?)re.Attribute("Id") ?? "";
        var m = Regex.Match(id, @"rId(\d+)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > maxId) maxId = n;
    }
    string newId = $"rId{maxId + 1}";
    rels.Root.Add(new XElement(rel + "Relationship",
        new XAttribute("Id", newId),
        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer"),
        new XAttribute("Target", "footer1.xml")));
    using (var s = relsEntry.Open()) { s.SetLength(0); rels.Save(s); }
    // sectPr footer reference
    var body = doc.Root!.Element(w + "body")!;
    var sectPr = body.Element(w + "sectPr")!;
    sectPr.Add(new XElement(w + "footerReference", new XAttribute(w + "type", "default"), new XAttribute(r + "id", newId)));
    // different odd/even not needed
}
static int _(string s) => 0;
