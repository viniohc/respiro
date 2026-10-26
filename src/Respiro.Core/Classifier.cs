namespace Respiro.Core;
public sealed class Classifier(IReadOnlyFileSystem fs)
{
    static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs",".rs",".py",".js",".ts",".tsx",".jsx",".java",".kt",".c",".cpp",".h",".go",".rb",".php",".swift",".xaml",".html",".css",".sql",".db",".sqlite",".sqlite3",".mdb",".bak",".pem",".key",".pfx",".p12",".env",".json",".yaml",".yml",".toml",".ini",".config",".xml",".gradle",".properties",".md",".txt",".pdf",".doc",".docx",".xls",".xlsx",".pptx",".jpg",".jpeg",".png",".heic",".mp4",".mov",".mp3",".wav",".zip",".7z" };
    internal static bool KnownProtected(string path)
    {
        string p=path.ToLowerInvariant(),name=Path.GetFileName(p),ext=Path.GetExtension(p);
        return ProtectedExtensions.Contains(ext)||p.Contains("e2.5")||p.Contains("e2-5")||p.Contains("\\e2-containment\\")||p.Contains("\\e25\\")||p.Contains("\\.git\\")||name==".git"||name.StartsWith(".env")||name.Contains("credential")||name.Contains("auth")||name.Contains("token")||name.Contains("secret")||name=="id_rsa"||p.Contains("\\.ssh\\")||p.Contains("\\.aws\\")||ext.EndsWith("-wal")||ext.EndsWith("-shm")||ext is ".vhd" or ".vhdx" or ".qcow2" or ".vdi" or ".vmdk" or ".img" or ".kdbx" or ".accdb" or ".cfg" or ".conf";
    }
    public Classification Classify(DiskEntry file)
    {
        var p=file.Path.ToLowerInvariant(); var name=Path.GetFileName(p); var ext=Path.GetExtension(p);
        var context=Context(file.Path);
        if(p.Contains("e2.5") || p.Contains("e2-5")) return new("Bloqueio de segurança",Nature.Protegido,"Referência E2.5: preservação obrigatória; política não validada.");
        if(p.Contains("\\.git\\") || name==".git") return new("Git / metadados",Nature.Protegido,"Metadados de repositório ou worktree; não são descartáveis.");
        if(name.StartsWith(".env") || name.Contains("credential") || name.Contains("auth") || name.Contains("token") || name=="id_rsa" || p.Contains("\\.ssh\\") || p.Contains("\\.aws\\") || ext.EndsWith("-wal") || ext.EndsWith("-shm"))
            return new("Credenciais e estado",Nature.Protegido,"Possíveis credenciais, configuração ou estado de banco. Preservar.");
        if(ProtectedExtensions.Contains(ext)) return new(context??"Dados e código protegidos",Nature.Protegido,"Código, dados pessoais, banco, configuração ou formato de conteúdo importante. Preservado mesmo dentro de cache ou pasta temporária.");
        if(ext is ".vhd" or ".vhdx" or ".qcow2" || context=="Android / AVD") return new(context??"Docker / WSL / disco virtual",Nature.Protegido,"Disco virtual ou dados de emulador: requer análise específica; não recuperável nesta versão.");
        if(context!=null)
        {
            bool generated = context is "Rust / Tauri" or "Gradle / build Android" or "Python / cache" or "npm / cache";
            return new(context,generated ? Nature.PotencialmenteRegeneravel : Nature.Desconhecido,generated ? "Estrutura e marcadores de ferramenta reconhecidos. Regeneração é hipótese; Git, uso, dependências e políticas ainda precisam de validação." : "Contexto reconhecido por marcadores; dependências ou saídas podem conter alterações e dados únicos.");
        }
        if(ext==".log") return new(file.Length>=100*1024*1024 ? "Logs grandes" : "Logs",Nature.Desconhecido,"Pode conter evidências importantes; tamanho e idade não autorizam limpeza.");
        if(ext is ".msi" or ".exe" or ".msix" or ".iso") return new("Instaladores e executáveis",Nature.Desconhecido,file.Modified<DateTime.UtcNow.AddDays(-180)?"Antigo por data de modificação; uso e possibilidade de reinstalação inconclusivos.":"Executável ou imagem; pode ser necessário. Não remover por extensão.");
        if(ext is ".tmp" or ".temp" || p.StartsWith(Path.GetTempPath().ToLowerInvariant())) return new("Temporários possíveis",Nature.Desconhecido,"Nome ou localização temporária é indício insuficiente; conteúdo não inspecionado.");
        if(file.Length>=1024L*1024*1024) return new("Arquivos grandes",Nature.Desconhecido,"Arquivo de pelo menos 1 GiB. Idade e tamanho não provam abandono.");
        return new("Outros / desconhecidos",Nature.Desconhecido,"Sem evidências suficientes para classificação segura.");
    }
    readonly Dictionary<string,string?> contextCache = new(StringComparer.OrdinalIgnoreCase);
    string? Context(string path)
    {
        var key=Path.GetDirectoryName(path)??path;
        if(key.EndsWith("__pycache__",StringComparison.OrdinalIgnoreCase))return DetectContext(path);
        if(contextCache.TryGetValue(key,out var cached))return cached;
        var detected=DetectContext(path);
        if(contextCache.Count>=50000)contextCache.Clear();
        contextCache[key]=detected;
        return detected;
    }
    string? DetectContext(string path)
    {
        // Only metadata markers are checked; no project scripts or file contents are executed/read.
        var dir=Path.GetDirectoryName(path); int depth=0;
        while(dir!=null && depth++<32)
        {
            string n=Path.GetFileName(dir).ToLowerInvariant(); var parent=Path.GetDirectoryName(dir);
            bool Has(string marker)=>fs.Exists(Path.Combine(dir,marker));
            bool ParentHas(string marker)=>parent!=null && fs.Exists(Path.Combine(parent,marker));
            if(n=="target" && ParentHas("Cargo.toml")) return "Rust / Tauri";
            if(n=="node_modules" && ParentHas("package.json")) return "Node / dependências";
            if(n=="_cacache" && Has("index-v5") && Has("content-v2")) return "npm / cache";
            if(n=="caches" && parent!=null && Path.GetFileName(parent)==".gradle" && (Has("modules-2") || Has("journal-1"))) return "Gradle / build Android";
            if((n=="build" || n==".gradle") && (ParentHas("build.gradle") || ParentHas("build.gradle.kts") || ParentHas("settings.gradle"))) return "Gradle / build Android";
            if(n.EndsWith(".avd") && Has("config.ini")) return "Android / AVD";
            if(Has("pyvenv.cfg")) return "Python / ambiente virtual";
            if(n=="__pycache__" && Path.GetExtension(path).Equals(".pyc",StringComparison.OrdinalIgnoreCase) && parent!=null && fs.Exists(Path.Combine(parent,Path.GetFileName(path).Split('.')[0]+".py"))) return "Python / cache";
            if(n is "output" or "dist" && (ParentHas("package.json") || ParentHas("Cargo.toml") || ParentHas("AGENTS.md"))) return "Projetos / saídas de agentes";
            if((n=="cache" || n=="gpucache" || n=="cache_data") && Has("index") && (Has("data_0") || Has("data_1"))) return "Cache de aplica\u00e7\u00e3o poss\u00edvel";
            if(n=="cache" && ParentHas("package.json")) return "Cache de aplicação possível";
            dir=parent;
        }
        return null;
    }
}
