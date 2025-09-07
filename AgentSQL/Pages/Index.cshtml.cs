
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.RegularExpressions;
using System.Text;

public class IndexModel : PageModel
{
    [BindProperty] public string? CreateSql { get; set; }
    [BindProperty] public List<string> Targets { get; set; } = new();
    [BindProperty] public bool OptProcs { get; set; } = true;
    [BindProperty] public bool OptDiff  { get; set; } = true;

    public bool IsPro { get; set; } = false;
    public Dictionary<string,string>? Results { get; set; }

    public readonly string[] AllDialects = new [] { "SQL Server", "PostgreSQL", "MySQL", "MariaDB", "SQLite", "Oracle", "Snowflake", "Spark SQL" };

    public void OnGet()
    {
        IsPro = HttpContext?.Request?.Cookies["AgentSQL_Pro"] == "1";
        if (Targets.Count == 0) Targets = AllDialects.ToList();
    }

    public void OnPost()
    {
        IsPro = HttpContext?.Request?.Cookies["AgentSQL_Pro"] == "1";
        if (!IsPro) { OptProcs = false; OptDiff = false; }
        if (Targets.Count == 0) Targets = AllDialects.ToList();
        Results = new();

        var parsed = Parser.ParseCreate(CreateSql ?? "");
        foreach (var t in Targets)
        {
            var gen = Generator.GenerateFor(t, parsed, OptProcs, OptDiff);
            Results[t] = gen;
        }
    }

    // --- naive parser (works for common CREATE TABLE forms) ---
    public record Column(string Name, string Type, bool IsPk, bool IsIdentity, bool IsNullable);
    public record Table(string Name, List<Column> Columns, List<string> PrimaryKeys);

    static class Parser
    {
        public static Table ParseCreate(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return new Table("Table", new(), new());
            var nameMatch = Regex.Match(sql, @"(?i)CREATE\s+TABLE\s+([^\s(]+)");
            var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "Table";

            // extract body between parentheses
            var bodyMatch = Regex.Match(sql, @"\((.*)\)", RegexOptions.Singleline);
            var body = bodyMatch.Success ? bodyMatch.Groups[1].Value : "";

            // split by commas not inside parens
            var parts = new List<string>();
            int depth=0; var sb=new StringBuilder();
            foreach(var ch in body){
                if (ch=='(') depth++;
                if (ch==')') depth--;
                if (ch==',' && depth==0){ parts.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            if (sb.Length>0) parts.Add(sb.ToString());

            var cols = new List<Column>();
            var pks = new List<string>();
            foreach (var raw in parts){
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (Regex.IsMatch(line, @"(?i)PRIMARY\s+KEY")){
                    // PRIMARY KEY (col,...)
                    var m = Regex.Match(line, @"\(([^)]+)\)");
                    if (m.Success){
                        var keys = m.Groups[1].Value.Split(',').Select(s=>s.Trim().Trim('[',']','"','`')).ToList();
                        pks.AddRange(keys);
                    }
                    continue;
                }
                var mcol = Regex.Match(line, @"^([`""\[\]]?)([A-Za-z0-9_]+)\1\s+([A-Za-z0-9_]+)(\([^)]+\))?\s*(.*)$");
                if (mcol.Success){
                    var colName = mcol.Groups[2].Value;
                    var colType = (mcol.Groups[3].Value + mcol.Groups[4].Value).Trim();
                    var rest = mcol.Groups[5].Value;
                    var isId = Regex.IsMatch(rest, @"(?i)IDENTITY|AUTO_INCREMENT|AUTOINCREMENT|SERIAL");
                    var nullable = !Regex.IsMatch(rest, @"(?i)NOT\s+NULL");
                    cols.Add(new Column(colName, colType, false, isId, nullable));
                }
            }
            // mark PKs
            foreach (var c in cols){
                if (pks.Contains(c.Name, StringComparer.OrdinalIgnoreCase)){
                    var ix = cols.IndexOf(c);
                    cols[ix] = c with { IsPk = true };
                }
            }
            return new Table(name, cols, pks);
        }
    }

    static class Generator
    {
        public static string GenerateFor(string dialect, Table t, bool procs, bool diff){
            var sb = new StringBuilder();
            sb.AppendLine($"-- Dialect: {dialect}");
            sb.AppendLine($"-- Table: {t.Name}");
            sb.AppendLine();

            var nonPk = t.Columns.Where(c=>!c.IsPk).ToList();
            var pks = t.Columns.Where(c=>c.IsPk).ToList();
            if (pks.Count==0 && t.Columns.Count>0){ pks = new(){ t.Columns.First() with { IsPk = true } }; }

            // Insert
            sb.AppendLine(Insert(dialect, t, nonPk));
            sb.AppendLine();
            // Update by PK
            sb.AppendLine(Update(dialect, t, nonPk, pks));
            sb.AppendLine();
            // Select with parameters
            sb.AppendLine(SelectByPk(dialect, t, pks));
            sb.AppendLine();

            if (procs){
                sb.AppendLine(Procs(dialect, t, nonPk, pks));
                sb.AppendLine();
            }
            if (diff){
                sb.AppendLine("-- DDL Diff (naive placeholder):");
                sb.AppendLine("-- Compare desired CREATE TABLE vs information_schema to produce ALTER statements (Pro).");
            }
            return sb.ToString();
        }

        static string Param(string dialect, string name, int index){
            return dialect switch {
                "SQL Server" => "@"+name,
                "PostgreSQL" => "$"+index,
                "MySQL" => "?",
                "MariaDB" => "?",
                "SQLite" => ":"+name,
                "Oracle" => ":"+name,
                "Snowflake" => "?",
                "Spark SQL" => "?",
                _ => ":"+name
            };
        }

        static string Insert(string dialect, Table t, List<Column> cols){
            var names = string.Join(", ", cols.Select(c=>c.Name));
            var vals = string.Join(", ", cols.Select((c,i)=>Param(dialect, c.Name, i+1)));
            return $"-- INSERT\nINSERT INTO {t.Name} ({names})\nVALUES ({vals});";
        }

        static string Update(string dialect, Table t, List<Column> cols, List<Column> pks){
            var sets = string.Join(", ", cols.Select((c,i)=>$"{c.Name} = {Param(dialect, c.Name, i+1)}"));
            var where = string.Join(" AND ", pks.Select((c,i)=>$"{c.Name} = {Param(dialect, c.Name, cols.Count + i + 1)}"));
            return $"-- UPDATE by PK\nUPDATE {t.Name} SET {sets} WHERE {where};";
        }

        static string SelectByPk(string dialect, Table t, List<Column> pks){
            var where = string.Join(" AND ", pks.Select((c,i)=>$"{c.Name} = {Param(dialect, c.Name, i+1)}"));
            return $"-- SELECT by PK\nSELECT * FROM {t.Name} WHERE {where};";
        }

        static string Procs(string dialect, Table t, List<Column> cols, List<Column> pks){
            var sb = new StringBuilder();
            if (dialect == "SQL Server"){
                sb.AppendLine($"-- SQL Server Stored Procedures");
                // Insert proc
                sb.AppendLine($"CREATE OR ALTER PROCEDURE dbo.{t.Name}_Insert");
                foreach (var c in cols) sb.AppendLine($"  @{c.Name} {MapSqlServerType(c.Type)},");
                if (cols.Count>0) sb.Remove(sb.Length-3, 1);
                sb.AppendLine("\nAS\nBEGIN\n  SET NOCOUNT ON;");
                sb.AppendLine($"  INSERT INTO {t.Name} ({string.Join(", ", cols.Select(c=>c.Name))}) VALUES ({string.Join(", ", cols.Select(c=>"@"+c.Name))});");
                sb.AppendLine("END;");
                sb.AppendLine();
                // Update proc
                sb.AppendLine($"CREATE OR ALTER PROCEDURE dbo.{t.Name}_Update");
                foreach (var c in cols) sb.AppendLine($"  @{c.Name} {MapSqlServerType(c.Type)},");
                foreach (var pk in pks) sb.AppendLine($"  @{pk.Name} {MapSqlServerType(pk.Type)},");
                sb.Remove(sb.Length-3, 1);
                sb.AppendLine("\nAS\nBEGIN\n  SET NOCOUNT ON;");
                sb.AppendLine($"  UPDATE {t.Name} SET {string.Join(", ", cols.Select(c=>$"{c.Name} = @{c.Name}"))} WHERE {string.Join(" AND ", pks.Select(pk=>$"{pk.Name} = @{pk.Name}"))};");
                sb.AppendLine("END;");
                sb.AppendLine();
                // Delete proc
                sb.AppendLine($"CREATE OR ALTER PROCEDURE dbo.{t.Name}_Delete");
                foreach (var pk in pks) sb.AppendLine($"  @{pk.Name} {MapSqlServerType(pk.Type)},");
                sb.Remove(sb.Length-3, 1);
                sb.AppendLine("\nAS\nBEGIN\n  SET NOCOUNT ON;");
                sb.AppendLine($"  DELETE FROM {t.Name} WHERE {string.Join(" AND ", pks.Select(pk=>$"{pk.Name} = @{pk.Name}"))};");
                sb.AppendLine("END;");
            }
            else if (dialect == "MySQL" || dialect == "MariaDB"){
                sb.AppendLine($"-- {dialect} Procedures");
                // Insert
                sb.AppendLine($"DELIMITER $$\nCREATE PROCEDURE {t.Name}_Insert({string.Join(", ", cols.Select(c=>$"IN p_{c.Name} {MapMySqlType(c.Type)}"))})\nBEGIN\n  INSERT INTO {t.Name} ({string.Join(", ", cols.Select(c=>c.Name))}) VALUES ({string.Join(", ", cols.Select(c=>$"p_{c.Name}"))});\nEND$$\nDELIMITER ;");
                // Update
                sb.AppendLine($"DELIMITER $$\nCREATE PROCEDURE {t.Name}_Update({string.Join(", ", cols.Concat(pks).Select(c=>$"IN p_{c.Name} {MapMySqlType(c.Type)}"))})\nBEGIN\n  UPDATE {t.Name} SET {string.Join(", ", cols.Select(c=>$"{c.Name} = p_{c.Name}"))} WHERE {string.Join(" AND ", pks.Select(pk=>$"{pk.Name} = p_{pk.Name}"))};\nEND$$\nDELIMITER ;");
                // Delete
                sb.AppendLine($"DELIMITER $$\nCREATE PROCEDURE {t.Name}_Delete({string.Join(", ", pks.Select(pk=>$"IN p_{pk.Name} {MapMySqlType(pk.Type)}"))})\nBEGIN\n  DELETE FROM {t.Name} WHERE {string.Join(" AND ", pks.Select(pk=>$"{pk.Name} = p_{pk.Name}"))};\nEND$$\nDELIMITER ;");
            }
            else if (dialect == "PostgreSQL"){
                sb.AppendLine("-- PostgreSQL functions");
                sb.AppendLine($"CREATE OR REPLACE FUNCTION {t.Name}_insert({string.Join(", ", cols.Select((c,i)=>$"{c.Name} {MapPgType(c.Type)}"))}) RETURNS void AS $$\nBEGIN\n  INSERT INTO {t.Name} ({string.Join(", ", cols.Select(c=>c.Name))}) VALUES ({string.Join(", ", cols.Select((c,i)=>"$"+(i+1)))});\nEND;\n$$ LANGUAGE plpgsql;");
                sb.AppendLine($"CREATE OR REPLACE FUNCTION {t.Name}_update({string.Join(", ", cols.Concat(pks).Select((c,i)=>$"{c.Name} {MapPgType(c.Type)}"))}) RETURNS void AS $$\nBEGIN\n  UPDATE {t.Name} SET {string.Join(", ", cols.Select(c=>$"{c.Name} = $"+(cols.IndexOf(c)+1)))} WHERE {string.Join(" AND ", pks.Select((pk,i)=>$"{pk.Name} = $"+(cols.Count+i+1)))};\nEND;\n$$ LANGUAGE plpgsql;");
                sb.AppendLine($"CREATE OR REPLACE FUNCTION {t.Name}_delete({string.Join(", ", pks.Select((pk,i)=>$"{pk.Name} {MapPgType(pk.Type)}"))}) RETURNS void AS $$\nBEGIN\n  DELETE FROM {t.Name} WHERE {string.Join(" AND ", pks.Select((pk,i)=>$"{pk.Name} = $"+(i+1)))};\nEND;\n$$ LANGUAGE plpgsql;");
            }
            else if (dialect == "Oracle"){
                sb.AppendLine("-- Oracle PL/SQL procedures");
                sb.AppendLine($"CREATE OR REPLACE PROCEDURE {t.Name}_insert({string.Join(", ", cols.Select(c=>$"{c.Name} IN {MapOracleType(c.Type)}"))}) AS BEGIN INSERT INTO {t.Name} ({string.Join(", ", cols.Select(c=>c.Name))}) VALUES ({string.Join(", ", cols.Select(c=>c.Name))}); END;");
                sb.AppendLine($"CREATE OR REPLACE PROCEDURE {t.Name}_update({string.Join(", ", cols.Concat(pks).Select(c=>$"{c.Name} IN {MapOracleType(c.Type)}"))}) AS BEGIN UPDATE {t.Name} SET {string.Join(", ", cols.Select(c=>$"{c.Name} = {c.Name}"))} WHERE {string.Join(" AND ", pks.Select(pk=>$"{pk.Name} = {pk.Name}"))}; END;");
                sb.AppendLine($"CREATE OR REPLACE PROCEDURE {t.Name}_delete({string.Join(", ", pks.Select(pk=>$"{pk.Name} IN {MapOracleType(pk.Type)}"))}) AS BEGIN DELETE FROM {t.Name} WHERE {string.Join(" AND ", pks.Select(pk=>$"{pk.Name} = {pk.Name}"))}; END;");
            }
            else {
                sb.AppendLine($"-- {dialect}: stored procedures not generated; use the parameterized DML above.");
            }
            return sb.ToString();
        }

        static string MapSqlServerType(string t){
            t = t.ToLowerInvariant();
            if (t.Contains("int")) return "INT";
            if (t.Contains("char")||t.Contains("text")||t.Contains("nchar")||t.Contains("nvar")) return "NVARCHAR(255)";
            if (t.Contains("date")||t.Contains("time")) return "DATETIME2";
            if (t.Contains("decimal")||t.Contains("numeric")) return "DECIMAL(18,2)";
            if (t.Contains("bit")||t.Contains("bool")) return "BIT";
            return "NVARCHAR(255)";
        }
        static string MapMySqlType(string t){
            t = t.ToLowerInvariant();
            if (t.Contains("int")) return "INT";
            if (t.Contains("char")||t.Contains("text")) return "VARCHAR(255)";
            if (t.Contains("date")||t.Contains("time")) return "DATETIME";
            if (t.Contains("decimal")||t.Contains("numeric")) return "DECIMAL(18,2)";
            if (t.Contains("bool")) return "TINYINT(1)";
            return "VARCHAR(255)";
        }
        static string MapPgType(string t){
            t = t.ToLowerInvariant();
            if (t.Contains("int")) return "integer";
            if (t.Contains("char")||t.Contains("text")) return "text";
            if (t.Contains("date")||t.Contains("time")) return "timestamp";
            if (t.Contains("decimal")||t.Contains("numeric")) return "numeric(18,2)";
            if (t.Contains("bool")) return "boolean";
            return "text";
        }
        static string MapOracleType(string t){
            t = t.ToLowerInvariant();
            if (t.Contains("int")) return "NUMBER";
            if (t.Contains("char")||t.Contains("text")) return "VARCHAR2";
            if (t.Contains("date")||t.Contains("time")) return "DATE";
            if (t.Contains("decimal")||t.Contains("numeric")) return "NUMBER";
            return "VARCHAR2";
        }
    }
}
