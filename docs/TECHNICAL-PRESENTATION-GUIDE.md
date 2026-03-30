# API Generator Technical Presentation Guide

Bu dokuman, projeyi teknik olarak anlatmak, mimariyi savunmak ve sunum sirasinda gelebilecek sorulara hazir olmak icin hazirlandi.

## 1. Kisa Sunum Ozeti

Bu proje iki ana parcadan olusur:

- VS Code extension
- .NET 8 tabanli generator CLI

Amac:

- SQL schema dosyasindan hizli API iskeleti uretmek
- Sirket standardini profile olarak ogrenmek
- Istenirse secili projeyi schema'ya gore yerinde guncellemek
- Istenirse stateless LLM ile uretilen kodu standarda daha uyumlu hale getirmek

Bir cumlelik teknik tanim:

> API Generator, VS Code uzerinden calisan, SQL schema + framework/profile + opsiyonel LLM girdilerini kullanarak standardize .NET API ureten bir platformdur.

## 2. Problemi Nasil Cozuyor

Klasik problem:

- Yeni API acmak zaman alir
- Her ekip farkli folder yapisi ve naming kullanir
- Swagger, authentication, repository, controller, appsettings gibi tekrar eden kodlar elle yazilir
- Sirket standardi kisilere bagli kalir

Bu sistem ne yapiyor:

1. SQL schema'yi parse ediyor
2. Tablo ve kolonlardan domain model cikariyor
3. Secilen framework/profile ile dosya planini olusturuyor
4. Template'leri render ediyor
5. Gerekirse mevcut projeden standard ogreniyor
6. Gerekirse LLM ile dosyalari refine ediyor
7. Sonucu manifest ve dokumantasyon ile kaydediyor

## 3. Mimari Yapisi

Sistem iki katmandan olusur.

### 3.1 Extension katmani

Sorumluluklari:

- Kullanicidan veri toplamak
- Framework, profile, target project, schema, connection string, LLM ayarlari gibi girdileri almak
- CLI'yi calistirmak
- Sonucu manifest uzerinden gostermek

Temel dosyalar:

- `extension/src/extension.ts`
- `extension/src/commands/registerCommands.ts`
- `extension/src/webview/ApiGeneratorPanel.ts`
- `extension/src/services/CliService.ts`
- `extension/src/services/ProfileStore.ts`
- `extension/src/services/LlmSettingsStore.ts`

### 3.2 Generator engine katmani

Sorumluluklari:

- CLI komutlarini almak
- SQL schema parse etmek
- Sirket standardi ogrenmek
- Generation plan olusturmak
- Template render etmek
- Dosya yazmak
- Manifest ve dokuman uretmek
- Opsiyonel LLM refine yapmak

Temel dosyalar:

- `generator-engine/Program.cs`
- `generator-engine/Commands/*`
- `generator-engine/SqlParser/*`
- `generator-engine/Analyzers/*`
- `generator-engine/Generators/*`
- `generator-engine/Templates/*`
- `generator-engine/Llm/OpenAiCompatibleLlmRefiner.cs`

## 4. Neden Iki Katmanli Tasarim Secildi

Bu soru gelirse verebilecegin cevap:

- UI ile kod uretimi birbirinden ayrildi
- VS Code bagimliligi generator engine tarafina sizmadi
- CLI tek basina da cagrilabilir hale geldi
- Ayni generator gelecekte farkli bir arayuzden de kullanilabilir
- Test ve bakim maliyeti azaldi

Kisa teknik savunma:

> Extension orchestration yapar, generator engine is kurallarini tasir.

## 5. Extension Tarafinin Calisma Akisi

Kullanicinin gordugu ana panel `ApiGeneratorPanel.ts` icinde uretilir.

Akis:

1. Extension aktive olur
2. Komutlar register edilir
3. Sidebar/webview acilir
4. Form verileri toplanir
5. `CliService` uzerinden `dotnet ApiGenerator.Cli.dll ...` calistirilir
6. Cikti klasorundeki `generation-manifest.json` okunur
7. Sonuclar panelde gosterilir

Onemli detay:

- Extension source code uretmez
- Sadece CLI'yi calistirir
- Sonucu manifest ile okur

Bu, extension tarafini ince ve daha stabil tutar.

## 6. CLI Komut Yapisi

Ana komutlar:

- `generate`
- `learn`
- `analyze`
- `document`

`Program.cs` icinde `System.CommandLine` ile tanimlanmistir.

### 6.1 generate

Temel kullanim:

```powershell
dotnet ApiGenerator.Cli.dll generate --schema examples/users.sql --output GeneratedApi
```

Ek opsiyonlar:

- `--framework`
- `--profile`
- `--project`
- `--connection-string`
- `--llm-enabled`
- `--llm-url`
- `--llm-model`
- `--llm-token`
- `--overwrite-mode`
- `--dry-run`

### 6.2 learn

Mevcut projeden reusable standard profile cikartir.

### 6.3 analyze

Projeyi analiz eder ama kalici generation yapmaz.

### 6.4 document

Dokumantasyon uretir.

## 7. SQL Parser Nasil Calisiyor

Parser tarafi basit ve deterministik tutuldu.

Temel model:

- `DatabaseSchema`
- `TableDefinition`
- `ColumnDefinition`

Parser'in bugunku gorevi:

- `CREATE TABLE` bloklarini bulmak
- Kolonlari ayirmak
- `NOT NULL`, `PRIMARY KEY`, `DEFAULT`, type, length gibi bilgileri cikarmak

Bugunku guclu yani:

- Basit SQL Server benzeri schema'larda hizli ve acik calisir

Bugunku siniri:

- Ayrik constraint tanimlari
- Karma dialect farklari
- GeliÅŸmis foreign key cozumlemesi

Bu soru gelirse soyle:

> Parser bugun MVP seviyesinde deterministik calisiyor; hedef SQL generator degil, schema'dan kod modeli cikarabilmek.

## 8. StandardProfile Nedir

Sistemin en kritik kavrami `StandardProfile`.

Bu nesne sunlari tasir:

- mimari tipi
- framework secimi
- folder kurallari
- naming rules
- pattern bilgileri
- controller standardi
- logging standardi
- template override'lari
- shared file template'leri
- LLM kurallari

Yani profil, "bu sirket veya bu proje nasil kod yaziyor?" sorusunun JSON cevabidir.

Bu sayede sistem sadece kod ureten degil, standard transfer eden bir araca donusuyor.

## 9. Learn Ozelligi Nasil Calisiyor

`RoslynProjectAnalyzer` secilen proje veya solution'u analiz eder.

Cikardigi seyler:

- folder yapisi
- controller veya endpoint stili
- repository/service isimlendirme deseni
- logging helper kullanimi
- swagger extension yapisi
- appsettings ve runtime wiring sinyalleri
- unit test varligi
- template override ornekleri
- shared support file'lar

Pratikte bu ne demek:

- Kullanici mevcut bir projeyi secer
- Sistem o projeden tekrar kullanilabilir bir profil cikarir
- Sonraki generation'larda ayni standard kullanilabilir

Bu kisim sunumda en fark yaratan bolumlerden biridir.

## 10. Generate Akisinin Teknik Siralamasi

`CleanArchitectureSolutionGenerator` icindeki ana akis:

1. Profile yuklenir ve merge edilir
2. Framework preset cozulur
3. Gerekirse learned profile dahil edilir
4. Layout karari verilir
5. Entity template modelleri uretilir
6. Solution scaffold planlanir
7. Entity bazli source dosyalari planlanir
8. Framework artifacts planlanir
9. Shared files eklenir
10. Dokumantasyon planlanir
11. LLM aciksa refine yapilir
12. `GenerationFileWriter` dosyalari yazar
13. Manifest uretilir

Bu asamada generator dogrudan dosya yazmaz; once plan olusturur, sonra yazar.

Bu tasarimin faydasi:

- dry-run yapilabilir
- overwrite policy uygulanabilir
- manifest uretilebilir
- gelecekte diff preview eklemek kolay olur

## 11. Framework Pack ile Default Framework Arasindaki Fark

Bu soru buyuk ihtimalle gelir.

### Framework pack secildiginde

Sistem hazir bir preset kullanir.

Ornek:

- `minimal-api-swagger`
- `aspnet-controller-swagger`
- `enterprise-controller-loghelper-swagger`

Bu mod yeni proje yaratmak icin daha uygundur.

### Default Framework secildiginde

Sistem kullanicidan target project ister.

Ardindan:

- secilen projeyi analiz eder
- onun standardini ogrenir
- schema'ya gore o projeyi yerinde gunceller

Bu modun amaci yeni bir stil secmek degil, mevcut projenin stilini korumaktir.

## Standard Profile Secim Tablosu

Bu kisim, "hangi standard profile'i ne zaman secmeliyim?" sorusuna hizli cevap verir.

| Secenek | Ne Uygular | Ne Zaman Secilir | Default Framework ile Iliskisi | Dikkat Edilecek Nokta |
| --- | --- | --- | --- | --- |
| Built-in Default | Generator'in baz naming, folder ve controller kurallarini kullanir. Ekstra sirket overlay'i bindirmez. | Minimal preset ile sade kalmak istendiginde veya sadece generator'in genel davranisi istendiginde secilir. | En guvenli secenektir. Secilen target project standardini bozmadan onun uzerine calisir. | Kuruma ozel template, logging veya ozel controller kalibi dayatmaz. |
| GeneratedApiPolicy Standard - Rules Only | GeneratedApiPolicy projesinden ogrenilen naming, folder, route ve genel yazim standardini uygular. Ozel kod govdesi bindirmez. | Kurumsal standard korunsun ama kod govdesi generic generator template'leriyle uretilsin istendiginde secilir. | Target project standardinin uzerine ek kurallar bindirir. Sadece secilen projenin kendi stilini kullanmak istemiyorsan uygundur. | Minimal veya mevcut projedeki sade yapilari kismen daha katmanli hale itebilir. |
| GeneratedApiPolicy Standard - Exact Templates | GeneratedApiPolicy kurallariyla birlikte controller, service, repository ve Program icin ozel template override'lari uygular. | Cikti referans projeye olabildigince benzesin istendiginde secilir. | Target project uzerine en baskin overlay olarak davranir. Ogrenilen standardin ustune birebir kaliplar bindirir. | En opinionated secenektir. Minimal preset veya mevcut proje stilini fazla zorlayabilir. |
| Workspace Default Standard | Repo icindeki clean-architecture agirlikli klasor ve naming standardini uygular. | Sirket template'i istemeyip daha duzenli ve katmanli bir yapi istendiginde secilir. | Target project uzerine clean-architecture egilimli bir overlay bindirir. | Daha duz veya minimal projelerde folder beklentisini degistirebilir. |

### Hangisini Ne Zaman Secerim

- Sadece secili projenin kendi standardi korunsun istiyorsan Built-in Default
- Kurum kurallari uygulansin ama kod govdesi birebir kopyalanmasin istiyorsan GeneratedApiPolicy Standard - Rules Only
- Cikti referans projeye en yakin olsun istiyorsan GeneratedApiPolicy Standard - Exact Templates
- Daha clean architecture agirlikli bir klasor duzeni istiyorsan Workspace Default Standard

## 12. Minimal Preset'in Ozelligi

Mevcut minimal preset ozellikle sade tutuldu.

Ozellikleri:

- tek solution icinde tek API projesi
- folder yapisi daha basit
- controller icinde endpointler
- request/response class ayrimi yok
- model db objesi ile ayni
- repository db islemlerini yapar
- appsettings ve launchSettings uretilir
- SQL Server + Windows Authentication kurgusu desteklenir

Bu sade yapi kullanicinin ozellikle istedigi "minimum boilerplate" senaryosu icin tasarlandi.

## 13. In-place Update Nedir

Default Framework modunda sistem yeni proje acmak yerine secilen projeyi update eder.

Bu ozellik su nedenle onemli:

- mevcut kurumsal projeyi sifirdan uretmek istemiyoruz
- sadece schema'ya gore yeni entity/controller/repository gibi parcalari eklemek veya guncellemek istiyoruz
- mevcut mimari dili korunuyor

Bu karar sunumda "adaptasyon" avantajini gosterir.

## 14. LLM Su An Ne Icin Kullaniliyor

LLM sistemin zorunlu parcasi degil.

LLM'nin bugunku rolu:

- Template ile uretilen dosyalari refine etmek
- Naming, logging, swagger, controller dili gibi konularda profile daha cok yaklastirmak
- Tek tek dosyalari stateless sekilde revize etmek

Onemli:

- LLM olmadan sistem calisir
- LLM sadece ikinci bir "uyumlulastirma" katmanidir

## 15. Neden Stateless LLM Kullaniliyor

Bu soru kritik.

Cevap:

- Tekrar uretilebilirlik daha yuksek
- Gizli oturum bagimliligi yok
- Prompt kapsami acik
- Her dosya ayni kural seti ile islenir
- Kurumsal guven ve audit acisindan daha savunulabilir

LLM'e verilen baglam:

- mevcut dosya icerigi
- `STANDARD-PROFILE.json`
- prompt package
- artifact bilgisi

Verilmeyen sey:

- onceki sohbet hafizasi
- gizli context
- oturum bagimli state

Bu nedenle "stateful agent" degil, "stateless refine pass" mantigi vardir.

## 16. LLM Kullanimi UI'da Nasil Tasarlandi

LLM tabinda:

- `LLM Kullan` tick'i var
- tick kapaliysa LLM kapali
- tick aciksa URL, model, token zorunlu
- token VS Code secret storage icinde tutulur

Bu tasarim iki hedefe hizmet eder:

- LLM'yi opsiyonel tutmak
- aktif oldugunda eksik konfig ile calismayi engellemek

## 17. Generation Manifest Neden Onemli

`generation-manifest.json` sistemin operasyonel omurgasidir.

Manifest su bilgileri tasir:

- solution name
- output path
- profile/framework bilgisi
- entity count
- overwrite mode
- olusan/guncellenen dosyalar
- LLM ozeti

Sunumda bunu soyle:

> Manifest, generator'in ekrana anlattigi seyin kalici ve makinece okunabilir versiyonudur.

## 18. Overwrite Modlari Neden Var

Sistemde uc politika vardir:

- `Skip`
- `Overwrite`
- `Fail`

Amaci:

- guvenli regeneration
- mevcut dosyalari kontrolsuz ezmemek
- CI veya otomasyon senaryolarinda daha kontrollu davranmak

Bu tasarim gelecekte diff preview ozelligi icin de zemin hazirlar.

## 19. Teknik Olarak Guclu Yanlar

Sunumda ozellikle vurgulayabilecegin guclu yanlar:

- UI ile generator engine ayrik
- Template bazli uretim var
- Sirket standardi profile donusturulebiliyor
- Mevcut projeden ogrenme yetenegi var
- LLM opsiyonel ve stateless
- Manifest ile izlenebilirlik var
- Default mode ile in-place update var
- Framework pack ile hizli baslangic var

## 20. Teknik Olarak Zayif veya Gelistirilecek Yanlar

Bu kisimda durust olmak guven yaratir.

Bugunku sinirlar:

- SQL parser hala MVP seviyesinde
- Extension su an workspace icindeki CLI'ya bagimli
- Diff preview henuz yok
- Test coverage sinirli
- Full standalone packaging henuz tamamlanmadi
- Bazi enterprise senaryolari template override mantigina bagli

Bu soru gelirse soyle:

> Mimari bu eksikleri kaldirabilecek sekilde ayrik tasarlandi; kisit bugun implementasyon olgunlugu, tasarim siniri degil.

## 21. Sunum Icin Onerilen Demo Akisi

5-7 dakikalik bir demo icin:

1. Kisa problem tanimi yap
2. Extension panelini goster
3. `examples/users.sql` sec
4. Bir framework pack ile yeni proje uret
5. `docs/`, `src/`, `generation-manifest.json` dosyalarini goster
6. Sonra `Default Framework` sec
7. Mevcut bir proje sec ve schema'ya gore update mantigini anlat
8. Son olarak `LLM Kullan` acik/kapali farkini goster

## 22. 2 Dakikalik Teknik Anlatim Metni

Asagidaki metni kisa sunum acilisi olarak kullanabilirsin:

> Bu sistem iki katmanli calisiyor: ustte VS Code extension var, altta ise .NET 8 ile yazilmis generator engine bulunuyor. Extension sadece kullanicidan veri topluyor ve CLI'yi cagiriyor. Asil is kurallari parser, analyzer ve generator tarafinda. SQL schema once yapisal modele cevriliyor, sonra secilen framework veya learned company profile ile generation plan olusturuluyor, template'ler render ediliyor ve manifest uretiliyor. Istenirse bu akisa stateless LLM refine passi da eklenebiliyor. Boylece hem hizli scaffold uretebiliyoruz hem de sirket standardini tekrar kullanilabilir hale getirebiliyoruz.

## 23. Muhtemel Sorular ve Hazir Cevaplar

### Soru: Neden dogrudan extension icinde source code uretmiyorsunuz

Cevap:

- VS Code bagimliligini generator katmanina tasimamak icin
- CLI'nin tek basina da kullanilabilir olmasi icin
- test ve bakimi kolaylastirmak icin

### Soru: Neden profile ihtiyac var

Cevap:

- her kurumun folder, naming, logging, swagger, DI stili farkli
- bu farklari kodun icine gommek yerine JSON profile tasiyoruz
- boylece standard tasinabilir oluyor

### Soru: LLM olmasa da sistem calisir mi

Cevap:

- evet
- LLM opsiyoneldir
- template + analyzer tabanli akisin uzerine refine katmani olarak gelir

### Soru: Neden stateless LLM

Cevap:

- tekrarlanabilirlik
- daha denetlenebilir bir prompt modeli
- gizli hafiza bagimliligini kaldirma

### Soru: Default Framework ile framework pack farki ne

Cevap:

- framework pack hazir bir preset'tir ve genelde yeni proje yaratir
- default framework ise secilen mevcut projeyi analiz edip ayni stilde yerinde update eder

### Soru: Roslyn neden kullaniliyor

Cevap:

- naming ve folder heuristics tek basina yeterli degil
- mevcut projeden daha zengin pattern cikarmak icin source analizi gerekiyor

### Soru: Bu sistem neden template-driven

Cevap:

- sabit string concat ile kod uretmek yerine daha degistirilebilir ve test edilebilir bir yapi saglar
- profile override mantigiyla daha uyumlu calisir

### Soru: En buyuk risk ne

Cevap:

- SQL parser ve enterprise varyasyonlarin dogru yakalanmasi
- bu nedenle parser, analyzer fallback ve manifest odakli yaklasim secildi

### Soru: Bu extension'i marketplace'e koyarsak calisir mi

Cevap:

- bugunku haliyle extension local `generator-engine` klasorune bagimli
- gercek standalone dagitim icin CLI'yi extension icine paketlemek gerekir

## 24. Teknik Soru Gelirse Kod Uzerinden Nereye Gitmeliyim

Hizli referans listesi:

- Komut kayitlari: `extension/src/commands/registerCommands.ts`
- UI ve form akisi: `extension/src/webview/ApiGeneratorPanel.ts`
- CLI cagrisi: `extension/src/services/CliService.ts`
- CLI giris noktasi: `generator-engine/Program.cs`
- SQL parse: `generator-engine/SqlParser/SqlSchemaParser.cs`
- Profil modeli: `generator-engine/Analyzers/StandardProfile.cs`
- Mevcut projeden ogrenme: `generator-engine/Analyzers/RoslynProjectAnalyzer.cs`
- Uretim orkestrasyonu: `generator-engine/Generators/CleanArchitectureSolutionGenerator.cs`
- LLM refine: `generator-engine/Llm/OpenAiCompatibleLlmRefiner.cs`

## 25. Sunumda Ozguvenli Durmak Icin Son Not

Sunumda tum detaylari ezberlemek zorunda degilsin. Asagidaki cizgiyi koruman yeterli:

- Bu sistem UI degil, generator mantigi agirlikli bir platform
- Merkez kavram `StandardProfile`
- Ayirt edici ozellik `learn + in-place update + optional stateless LLM`
- Mimari karar: extension orchestration, CLI generation
- Zayif nokta: packaging ve parser olgunlugu
- Guclu nokta: standardizasyon, tekrar kullanilabilirlik, deterministik generation

## 26. Kisa Sonuc Cumlesi

Sunumu kapatmak icin kullanabilecegin cumle:

> Bu proje sadece kod ureten bir arac degil; kurumsal API standardini ogrenip tekrar uygulayabilen, bunu da template, profile ve opsiyonel stateless LLM yaklasimiyla yapan bir platform.
