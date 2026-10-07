# Backlog

Tek kaynak backlog. Her task bir gereksinime (`docs/PRODUCT-REQUIREMENTS.md` FR/NFR) bağlıdır ve kabul kriteri
otomatik testlerle doğrulanır. Döngü: sıradaki açık taskı geliştir → `tools/run-all-tests.sh` (UI E2E dahil) baştan
çalıştır → bug bulunursa "Bulunan Buglar" bölümüne ekle, düzelt, testleri baştan başlat → tüm testler geçince sonraki task.

Durum: `[ ]` açık, `[x]` tamamlandı ve testlerle doğrulandı.

## 1. Gereksinim Özeti

| Kod | Gereksinim | Mevcut durum (analiz) | Task |
| --- | --- | --- | --- |
| FR-1 | SQL'den Clean Architecture CRUD üretimi | Çalışıyor; bileşik anahtar CRUD'u ilk kolonu kullanıyor, PK'sız tablolar sessizce ilk kolonu anahtar yapıyor | BE-05, BE-06 |
| FR-2/3 | Şirket standardı öğrenme | Çalışıyor (QA backlog'da düzeltildi) | QA-05 |
| FR-4 | Özel endpoint reçeteleri (GetByCode, GetActiveList, Search, BulkInsert, GetByDateRange) | Yok; "Endpoint Ekle" modu normal üretimi çalıştırıyor | BE-04, UI-03 |
| FR-5 | xUnit + Moq + FluentAssertions testleri, success/validation/not found/exception | Testler `true.Should().BeTrue()` yer tutucusu | BE-01 |
| FR-6 | Swagger, XML yorumları, response metadata, auth noktaları | XML yorumu ve `GenerateDocumentationFile` yok | BE-02, BE-03 |
| FR-7 | README / API / ARCHITECTURE dokümanları | Var; request/response alan şeması ve FK bilgisi yok | BE-11, BE-12 |
| FR-8 | Extension komutları, wizard, log, özet, dosya yolları | Var; doğrulama, önizleme, dosya açma, hata/tekrar akışı eksik; metinlerde bozuk karakter | UI-01…UI-08 |
| FR-9 | Şablon özelleştirme ve doğrulama | Profil override var; workspace override ve ön doğrulama yok | BE-08 |
| FR-10 | Güvenli yeniden üretim, önizleme | Skip/Overwrite/Fail var; UI önizlemesi yok | UI-05 |
| NFR-2 | 10 entity < 5 sn, 50 entity < 10 sn | Ölçülmedi | BE-14 |
| NFR-3 | Aksiyon alınabilir hata mesajları | Kısmen; hata kategorisi/çıkış kodu yok | BE-07 |
| NFR-5 | Yapılandırılmış log ve özet | Yok | BE-07 |
| NFR-6 | Yol doğrulama, şablondan script çalıştırma yok | SharedFiles ile çıktı dışına yazılabiliyor | BE-13 |
| Profil sürümleme | Şema sürüm politikası | Yok | BE-09 |
| Parser | Satır bazlı tanılama, FK hedefi | Yok | BE-10, BE-11 |

## 2. Backend (CLI / generator-engine)

- [x] **BE-01 Gerçek birim testleri (FR-5).** `UnitTests.sbncs` yer tutucularını kaldır. Servis katmanı olan
  presetlerde servis testleri (Moq ile repository), servis katmanı olmayan (single-api) presetlerde controller
  testleri üret. Her entity için: GetAll success, GetById success, GetById not found, Create success,
  Create validation failure (null request → `ArgumentNullException`), Update not found, Delete not found,
  repository exception propagation. Servis/controller'a `ArgumentNullException.ThrowIfNull` guard'ı ekle.
  Kabul: üretilen testlerde `placeholder` yok, Moq kullanılıyor, tüm presetlerde `dotnet test` yeşil.
- [x] **BE-02 XML yorumları ve Swagger metadata (FR-6).** Api csproj'a `GenerateDocumentationFile` + `NoWarn 1591`,
  controller action'larına `/// <summary>`, Swagger'a `IncludeXmlComments`. Kabul: swagger.json içinde action
  summary'leri görünür.
- [x] **BE-03 Kimlik doğrulama entegrasyon noktası (FR-6).** Windows auth kapalıyken de `UseAuthorization` ve
  `AddAuthorization` iskeleti olsun; README'de auth genişletme notu. Kabul: tüm presetler derlenir, runtime CRUD geçer.
- [x] **BE-04 Endpoint reçeteleri ve `add-endpoint` komutu (FR-4).** `add-endpoint --project <çözüm> --entity <Ad>
  --recipe <GetByCode|GetActiveList|Search|BulkInsert|GetByDateRange> [--field <Kolon>]`. Reçeteler metin
  mutasyonu yerine ayrı partial dosyalar üretir (`UsersController.GetByEmail.cs` vb.); hedef sınıflar partial
  değilse Roslyn ile `partial` eklenir. Repository arayüz/uygulama, servis arayüz/uygulama (varsa), controller,
  birim testleri ve `docs/API-DOCUMENTATION.md` güncellenir; reçete `api-generator.endpoints.json` içine kaydedilir,
  komut idempotenttir. Alan tipi reçeteye uygun değilse (ör. GetByDateRange için tarih değil) anlaşılır hata.
  Kabul: her reçete her controller presetinde derlenir, testleri geçer ve HTTP üzerinden çalışır.
- [x] **BE-05 Bileşik anahtar CRUD'u (FR-1).** Karar: route her anahtar kolonu için segment içerir
  (`/api/CompositeKey/{tenantId}/{itemId}`); repository/servis/controller/test/doküman/Postman bu anahtarlarla
  çalışır. Kabul: aynı ilk anahtarı paylaşan iki satır ayrı ayrı okunur/güncellenir/silinir (runtime testi).
- [x] **BE-06 PK'sız tablolar için uyarı (FR-1, NFR-3).** İlk kolon anahtar kabul edilmeye devam eder ama manifest
  `Warnings` listesine ve konsola tablo adıyla uyarı yazılır. Kabul: CLI case testi uyarıyı görür.
- [x] **BE-07 Yapılandırılmış log, uyarılar ve çıkış kodları (NFR-3, NFR-5).** `--log-format text|json`;
  json modunda her olay tek satır JSON (`level`, `event`, `message`, ...). Çıkış kodları: 0 başarı, 2 geçersiz
  girdi, 3 çakışma (Fail modu), 1 beklenmeyen hata. Manifest'e `Warnings`. Kabul: CLI case testleri kodları ve
  JSON satırlarını doğrular.
- [x] **BE-08 Şablon doğrulama ve workspace override (FR-9).** Üretimden önce tüm yerleşik şablonlar ve profil
  override'ları parse edilir; hata şablon anahtarı ve satır ile raporlanır, hiçbir dosya yazılmaz.
  `--templates <klasör>` ile aynı isimli dosyalar yerleşik şablonları ezer. Kabul: bozuk override exit 2 ve
  şablon adıyla hata; override klasörü çıktıya yansır.
- [x] **BE-09 Profil şema sürümü politikası.** Desteklenen ana sürüm 2; daha yeni ana sürüm → exit 2 ile hata;
  sürüm yoksa uyarı. Politika `docs/` altında yazılı. Kabul: birim + CLI testleri.
- [x] **BE-10 Parser tanılama (FR-1, NFR-3).** Okunamayan kolon satırları ve bilinmeyen tipler (string'e düşen)
  satır numarasıyla uyarı olarak raporlanır. Kabul: birim testi satır numarasını doğrular.
- [x] **BE-11 Foreign key hedefi.** `REFERENCES T(C)` ve `FOREIGN KEY (...) REFERENCES T(C)` hedefleri modele
  alınır, dokümanda ilişkiler tablosu üretilir. Kabul: birim testi + doküman içeriği testi.
- [x] **BE-12 Dokümanda request/response şemaları (FR-7).** API dokümanında her entity için alan tablosu
  (ad, tip, zorunlu, anahtar, uzunluk) ve reçete endpoint'leri. Kabul: smoke testi içerik kontrolü.
- [x] **BE-13 Güvenlik: yol doğrulama (NFR-6).** Plan içindeki hiçbir dosya çıktı kökünün dışına yazılamaz
  (`../`, mutlak yol); şema/çıktı yolları doğrulanır; Scriban `include` ile dosya okuma kapalı. Kabul: kötü niyetli
  profil exit 2 ile reddedilir ve çıktı dışında dosya oluşmaz.
- [x] **BE-14 Performans (NFR-2).** 10 ve 50 tabloluk şemalar için süre ölçen test; 50 tablo < 10 sn.

## 3. UI (VS Code extension)

- [x] **UI-01 Bozuk metinler.** LLM sekmesindeki çift kodlanmış Türkçe metinler ("EÅŸzamanlÄ±lÄ±k" vb.),
  "Şema Seçç" yazım hatası, `addDraftSütun` gibi ASCII olmayan DOM id'leri düzeltilir. Kabul: kaynakta mojibake
  deseni yok; E2E testinde metinler doğru görünür.
- [x] **UI-02 Platformdan bağımsız CLI çalıştırma.** Windows dışında `dotnet ApiGenerator.Cli.dll` ile çalışsın;
  `package.json` içindeki `os: win32` kısıtı `npm ci`'yi Linux/macOS'ta kırıyor, kaldırılır. Kabul: extension
  Linux'ta kurulur ve E2E'de gerçek CLI'ı çalıştırır.
- [x] **UI-03 Endpoint Ekle formu (FR-4, FR-8).** Endpoint modunda çözüm klasörü, entity, reçete ve alan alanları;
  CLI `add-endpoint` çağrılır, sonuç özetlenir. Kabul: E2E testinde reçete eklenir ve dosyalar listelenir.
- [x] **UI-04 Form doğrulama (FR-8).** Mod bazında zorunlu alanlar (şema, çıktı, referans proje, entity...) boşsa
  çalıştırma yapılmaz, alan altında hata gösterilir. Kabul: E2E testinde boş form → hata mesajları, CLI çağrılmaz.
- [x] **UI-05 Önizleme (FR-10).** "Önizle" butonu `--dry-run` çalıştırır, dosyaları durumlarıyla (created /
  updated / conflict) gösterir, diske yazmaz. Kabul: E2E önizleme sonrası çıktı klasörü yok.
- [x] **UI-06 Sonuç ve hata akışı (FR-8).** Sonuç listesinde her dosya durum rozetiyle; tıklanınca dosya editörde
  açılır. Hata durumunda stderr okunur bir kartta, "Tekrar Dene" butonuyla. Kabul: E2E testleri.
- [x] **UI-07 Son kullanılanlar (FR-8).** Son 5 şema ve çıktı yolu hatırlanır ve öneri olarak sunulur.
- [x] **UI-08 Uyarıların gösterimi.** Manifest `Warnings` sonuç ekranında listelenir.

## 4. Kalite / Test Altyapısı

- [x] **QA-01 Webview E2E altyapısı ("computer use").** Gerçek extension kodu (vscode API stub'ı ile) + gerçek
  CLI + Chromium (Playwright). Mesaj köprüsü, ekran görüntüleri `artifacts/ui-e2e/`.
- [x] **QA-02 E2E senaryoları.** Her mod: create/generate (preset ile), Default Framework (referans proje),
  learn, document, endpoint; doğrulama, önizleme, hata + tekrar, LLM ayar kaydı, şema tasarımcısı
  (yükle / tablo ekle / sil).
- [x] **QA-03 `run-all-tests.sh` tüm suit'leri içerir** (UI E2E dahil) ve ilk hatada durur.
- [x] **QA-04 CI.** GitHub Actions iş akışı `tools/run-all-tests.sh`'ı çalıştırır.
- [x] **QA-05 Renderer ve analyzer birim testleri.**

## 5. Bulunan Buglar

(Döngü sırasında bulunan buglar buraya eklenir ve düzeltilince işaretlenir. Önceki tur: `docs/TASK-LIST.md`
"QA Backlog" bölümü, BUG-1…BUG-13.)

- [x] **UB-1** Hatalı CLI çalışmasında webview `ReferenceError` ile çöküyordu (`setBoÅŸtaSummary` mojibake'li çağrı);
  hata ekranı hiç gösterilmiyordu.
- [x] **UB-2** Hata durumunda durum rozeti "Başarısız" yazıldıktan sonra tekrar "Boşta"ya dönüyordu.
- [x] **UB-3** `hidden` öznitelikli bölümler (Referans Proje, Ek Profil, kontrol listesi notu) CSS `display`
  kuralları yüzünden gizlenmiyordu; hazır paket seçiliyken alakasız alanlar görünüyordu.
- [x] **UB-4** Sonuç listesi yalnızca ilk 12 dosyayı gösteriyordu; dosya yolları HTML kaçışsız basılıyordu.
- [x] **UB-5** "API Generator" Output kanalı oluşturuluyor ama CLI logu hiç yazılmıyordu.
- [x] **UB-6** `schema-pick-error` mesajı webview'de işlenmiyordu (seçici hatası sessizce kayboluyordu).
- [x] **CB-1** Profildeki `SharedFiles.RelativePath` (`../../x`) ile çıktı klasörü dışına dosya yazılabiliyordu
  (path traversal); artık plan yazmadan önce doğrulanıyor.
- [x] **CB-2** Profil şablonlarındaki hata "inline-content" adıyla ve exit 1 ile raporlanıyordu; artık anahtar,
  satır:sütun ve exit 2 ile, hiçbir dosya yazılmadan raporlanıyor.
- [x] **CB-3** Bozuk profil JSON'u ham `JsonException` ile çıkıyordu; dosya adıyla anlaşılır hata veriliyor.
- [x] **CB-4** `--dry-run` çıktı klasörünü boş da olsa oluşturuyordu.
- [x] **CB-5** Katmanlı presetlerde `--windows-auth Enable` controller'a `[Authorize]` ekliyor ama kimlik doğrulama
  servisini kaydetmiyordu; her istek 500 dönüyordu (artık 401 + Negotiate challenge).
- [x] **CB-6** Katmanlı presetlerde create, IDENTITY olmayan anahtarı yok sayıyordu; her kayıt Id=0 alıyor, ikinci kayıt
  çakışıyordu. Artık istemci anahtarı korunuyor, IDENTITY anahtarını repository üretiyor, aynı anahtar 409 dönüyor;
  in-memory repository thread-safe.
- [x] **CB-7** EF repository'leri `EqualityComparer` ile sorgu yazıyordu; SQL Server sağlayıcısı bunu çeviremediği için
  GetById/Update/Delete gerçek veritabanında çalışmıyordu (InMemory'de görünmüyordu). Artık `FindAsync`; EF kontrolü
  sorgu çevirisini SQL Server sağlayıcısıyla doğruluyor.
- [x] **CB-8** Enterprise presetinde `--windows-auth Enable` sessizce yok sayılıyordu.
- [x] **CB-9** Postman koleksiyonunda path değişkenleri `{{id}}` olarak yazılıyordu (Postman bunu ortam değişkeni
  sanar); create gövdesinde zorunlu anahtar eksikti, Guid/short/TimeOnly gibi tipler için geçersiz örnekler vardı.
- [x] **CB-10** Dosya üretimi `Patterns.usesControllers`, Program şablonu `Framework.UseControllers` okuyordu; minimal API
  profillerinde endpoint modülleri üretilmiyor, proje derlenmiyordu. Tek kaynak `Framework.UseControllers`.
- [x] **CB-11** Üretilen kodda derleme uyarıları (tekrarlı using, learner'ın `id` XML param etiketi, fixture'da null
  dönüş); smoke/reçete derlemeleri artık `-warnaserror`.
- [x] **CB-12** Dry-run manifest durumu "would-created" yazılıyordu (dosyalar "would-create"); reçete hata mesajında
  "needs a a scalar" yazım hatası.
- [x] **UB-8** CLI bulunamazsa `run` mesajına hiç sonuç dönmüyor, UI "Çalışıyor" durumunda kalıyordu.
- [x] **UB-9** Endpoint modunda üretim ayarları (framework, SQL, çalışma zamanı, LLM) gösteriliyor ve komut normal
  üretimi çalıştırıyordu; learn/document modlarında da anlamsız sekmeler vardı.
- [x] **UB-10** Manifest dosyası değişiklik zamanına göre okunuyordu; dry-run (manifest yazmaz) sonuçları hiç
  gösterilemiyordu. Artık CLI JSON olaylarından okunuyor.
- [x] **CB-13** Learner örnek controller olarak bileşik anahtarlı bir entity seçerse onun anahtarlarını (`leftId`,
  `rightId`) öğrenilen şablona sabit yazıyordu; artık tek anahtarlı örnek tercih ediliyor.
- [x] **UB-7** Arayüz metinleri karışık dilde (SQL Schema / Summary / Runtime, İngilizce servis hataları) ve
  yanıltıcı adım açıklaması ("Varsayılan mod mevcut projeyi günceller").

## 6. Referans Projeden Sıfırdan Üretim (Default Framework)

İstek: Varsayılan proje (referans) seçiliyken "üzerine yaz" önceki üretimin üzerine ekleme yapıyordu; referans baz
alınarak sıfırdan üretilmeli ve referanstaki dosya/klasör örneklerine daha sıkı uyulmalı.

- [x] **RF-1** `--overwrite-mode Overwrite` sıfırdan üretir: önceki `generation-manifest.json` ve add-endpoint
  kayıtlarındaki, yeni planda olmayan dosyalar silinir (`deleted` / dry-run'da `would-delete`), boşalan klasörler
  kaldırılır, `api-generator.endpoints.json` temizlenir; üreticinin yazmadığı dosyalara dokunulmaz.
- [x] **RF-2** Özet ve manifest silinen dosya sayısını gösterir (`deleted: N`, `Summary.Deleted`).
- [x] **RF-3** Çıktı klasörü referans projenin kendisi ya da içi olamaz (exit 2); referans yalnızca okunur.
- [x] **RF-4** Learner repository arayüzlerinin klasörünü öğrenir (`Folders.repositoryInterfaces`, ör.
  `DataAccess/Repositories/Interfaces`); arayüzler veri erişim projesindeyse proje referans yönü
  Business → DataAccess olur.
- [x] **RF-5** Test klasörü ve test namespace'i referanstan öğrenilir (`test/X.Tests/Services`).
- [x] **RF-6** İstek adlarında fiilin konumu öğrenilir (`CustomerCreateRequest` → `{Entity}CreateRequest`).
- [x] **RF-7** Referansta DTO yoksa DTO üretilmez (`Patterns.usesDtos`).
- [x] **RF-8** Tekil entity adları (`Customer`, tablo `Customers`) ve çoğul özellik klasörleri (`Services/Customers`)
  öğrenilir (`Patterns.singularEntityNames`, `{{ EntityPluralName }}`).
- [x] **RF-9** Öğrenilen controller ve Program şablonlarındaki örneğe özgü `using` satırları namespace token'larına
  çevrilir (örnek entity adından türetilen yanlış namespace'ler derlenmiyordu).
- [x] **RF-10** UI: "Sıfırdan üret (önceki üretimi temizle)" seçeneği, açıklaması, "Silindi" metriği ve
  Silindi/Silinecek durum etiketleri.
- [x] **RF-11** Testler: `tools/cases/reference-acme` el yazımı referans; unit (learner, yapı, sıfırdan üretim, Skip,
  referans koruması, tekilleştirme), CLI vakaları (derleme `-warnaserror` + test), UI E2E senaryosu.
- [x] **RB-1** add-endpoint doküman satırlarını CRUD satırlarının hemen altına eklediği için sıra tersine dönüyor,
  Skip modunda yeniden üretim çakışma veriyordu; ayrıca aynı metot adı başka entity'de varsa satır eklenmiyordu.
- [x] **RF-12** Referansta model adını içeren klasörler (`Requests/Customer`, `Responses/CustomerModels`,
  `Repositories/CustomerRepositories`, `Controllers/CustomerOperations`, `Tests/CustomerTests`) aynen kopyalanıyor,
  yeni entity'lerin kodu örnek modelin klasörüne konuyordu. Learner örnek modeli referanstaki entity adlarından bulur,
  klasör adının içinde PascalCase sınırında geçen model adını `{{ EntityName }}` / `{{ EntityPluralName }}` yapar;
  her entity kendi klasörünü alır (`OrderModels`, `OrderRepositories` ...).
- [x] **RF-13** Referansta modül/alan gruplaması gibi iç içe klasörler (`Services/Sales/Customers`,
  `Models/Sales/Requests`, `Controllers/V1/Sales`) ve controller namespace'i (`Controllers.V1.Sales`) yeni projeye
  aynen taşınıyordu. Learner örnek dosyayı aynı türdeki diğer dosyalarla karşılaştırır: hepsinde ortak olan klasörler
  korunur, dosyadan dosyaya değişen klasörler model adı taşıyorsa token'a çevrilir, taşımıyorsa atılır; controller ve
  endpoint şablonlarının kendi namespace'i üretilen klasörden türetilir.
