# RCS — pilot istifadəçi bələdçisi

Bu qısa bələdçi 10 günlük sınaq (pilot) üçündür. Məqsəd sistemi **real işinizdə** yoxlamaqdır.

> **Vacib:** iş üsulunuzu RCS-ə uyğunlaşdırmağa çalışmayın. Əgər sistem sizin real iş qaydanızla uyğun gəlmirsə,
> bu, **bizim üçün ən dəyərli məlumatdır** — bildirin, biz sistemi dəyişəcəyik.

---

## 1. Sistemə giriş

RCS iki şəkildə quraşdırıla bilər. İnzibatçı sizə hansının olduğunu deyəcək.

**A) Öz noutbukunuzda (tək kompüter rejimi)**

1. İş masasındakı **RCS** ikonuna iki dəfə klikləyin.
2. Proqram özü işə düşür və brauzer avtomatik açılır. İlk açılış 10–20 saniyə çəkə bilər.
3. İkinci dəfə klikləsəniz, ikinci nüsxə açılmır — sadəcə brauzer yenidən açılır.
4. İşi bitirdikdə **RCS-i dayandır** ikonu ilə proqramı bağlaya bilərsiniz (məcburi deyil).
5. Nə isə səhv olarsa, ekranda izahlı bir pəncərə çıxır — mətni inzibatçıya göstərin.

Bütün məlumat **yalnız bu noutbukda** saxlanılır. Şəbəkədə heç nə paylaşılmır.

**B) Şəbəkə serverində**

1. Brauzerdə ünvanı açın: **https://rcs.example.lan** (dəqiq ünvanı inzibatçı verəcək).

**Hər iki halda:**

2. İstifadəçi adınızı və parolunuzu daxil edin.
3. İlk girişdə müvəqqəti parolu **öz parolunuzla əvəz etməlisiniz**. Ən azı 12 simvol; uzun və yadda qalan
   bir ifadə seçin (məsələn üç-dörd sözdən ibarət cümlə). Mürəkkəb işarələr tələb olunmur.

Parolunuz yalnız sizindir — heç kimlə bölüşməyin. Bütün əməliyyatlar sizin adınıza qeyd olunur.

İşiniz bitdikdə yuxarı sağdakı **Çıxış** düyməsini basın. 60 dəqiqə hərəkətsizlikdən sonra sistem özü çıxış edir.

Parolu unutsanız və ya hesab bloklansa (ardıcıl 5 səhv paroldan sonra 15 dəqiqəlik blok), **texniki inzibatçıya
şəxsən** müraciət edin. Telefon və ya e-poçtla parol göndərilmir.

## 2. İş (Case) yaratmaq

Bir iş həmişə **daxil olan məktubla** başlayır.

1. Sol menyuda **İşlər** → **Yeni iş**.
2. Müraciət edən təşkilatı seçin. Siyahıda yoxdursa, əvvəlcə **Təşkilatlar** → **Yeni təşkilat**.
3. Məktubun nömrəsini və tarixini, işin adını yazın.
4. Məsul əməkdaşı seçin (pilot müddətində bu, sizsiniz).
5. **Yadda saxla**.

İş açıldıqdan sonra ekranın mərkəzində **iş xəritəsi** görünür: məktub, sorğular, cavablar və tələblər.
Hər hansı elementə klikləyin — sağ tərəfdəki **müfəttiş panelində** onun təfərrüatları və əməliyyatları açılır.

## 3. Sorğu (Request) göndərmək

Kənar təşkilatdan rəy və ya şərt soruşduqda:

1. İş səhifəsində **Sorğu əlavə et**.
2. Təşkilatı, məktubun nömrəsini və göndərilmə tarixini yazın.
3. Sistem **son tarixi** avtomatik təklif edir (göndərilmə tarixi + 10 təqvim günü). Lazım olsa dəyişin.

Vaxtı keçən sorğular ana səhifədə və iş siyahısında işarələnir. Sistem heç kimə məktub göndərmir —
yazışma həmişəki kimi kənarda aparılır, RCS yalnız **qeyd edir**.

## 4. Cavab (Response) qeyd etmək

Təşkilatdan cavab məktubu gəldikdə:

1. Müvafiq sorğunu seçin → **Cavab qeyd et**.
2. Məktubun nömrəsi, tarixi və cavabın **növünü** (rəy, əlavə tələb, məlumat…) seçin.
3. **Nəticəni** ayrıca seçin (razılıq, imtina, şərtlə razılıq…).
4. Cavab qəti (yekun) deyilsə, müvafiq qeydi işarələyin.

## 5. Tələb (Requirement)

Cavabda şərt irəli sürülübsə, həmin cavabdan **tələb** yaradın: nə tələb olunur, bloklayıcıdır ya yox, son tarix.

Tələb yerinə yetirildikdə **Yerinə yetirildi** deyin və əsası göstərin — adətən **sənəd** və ya başqa təşkilatın
cavabı. Tələb artıq aktual deyilsə **ləğv et**, məsuliyyətdən azad edilirsə **güzəşt et** seçin.

## 6. Sənədlər: yükləmə, ön baxış, endirmə

Sənəd həmişə **bir kontekstə** əlavə olunur (məktub, sorğu, cavab, tələb, yekun qərar və ya işin özü).

1. Müvafiq bölmədə **Fayl yüklə**.
2. Faylı seçin, başlıq və sənəd növünü yazın. Maksimum **500 MB**.
3. Yükləndikdən sonra sənəd sətrində vəziyyət görünür:
   - **Ön baxış hazırlanır** — sistem arxa planda hazırlayır, gözləyin;
   - **Ön baxış hazırdır** — **Baxış** düyməsi ilə açın;
   - **Ön baxış mümkün deyil** — bu format üçün ön baxış yoxdur (məsələn DWG, ArchiCAD). Sənəd yenə də
     saxlanılır və **endirilə bilər**;
   - **Ön baxış alınmadı** — hazırlanma uğursuz olub; **Yenidən cəhd et** mümkündür. **Orijinal sənəd
     heç vaxt zədələnmir.**
4. **Baxış** pəncərəsində: səhifələr arası keçid, böyütmə/kiçiltmə, tam ekran.
5. **Endir** düyməsi həmişə **orijinal faylı** verir.

PDF, Word, Excel, şəkillər (PNG/JPEG/WebP) və KMZ üçün ön baxış var. Sənədin özü heç vaxt dəyişdirilmir.

> **Tək kompüter rejimində ön baxış işləmir.** Bu rejimdə bütün sənədlər normal yüklənir, siyahıda görünür və
> **endirilir**, sadəcə sistem daxilində baxış pəncərəsi açılmır — faylı endirib öz proqramınızda açın.

## 7. Yekun qərar (Final Result), bağlama və yenidən açma

1. İş hazır olduqda **Yekun qərar** → qərar növü, xülasə və əsaslandırma.
2. İmzalanmış qərar sənədini yükləyin.
3. **Təsdiq et** — bu, Rəhbər səlahiyyətidir (pilot müddətində sizdə olan rollardan biridir).
4. Sonra **İşi bağla** → bağlanma növünü və qeydi yazın.
5. Sonradan yeni məktub gəlsə, **İşi yenidən aç** → səbəbi mütləq yazılır.

Bağlanmış işdəki məlumat silinmir; yenidən açılan iş **eyni işdir**, yeni nömrə almır.

## 8. Nəsə səhv görünəndə nə etməli

1. **Məlumatı silməyə çalışmayın** — sistemdə silinmə yoxdur, səhvlər **düzəliş** yolu ilə aparılır
   (səhv sənədi geri çəkmək, əlaqəni düzəltmək və s.).
2. Ekranı dəyişmədən **ekran şəkli** çəkin.
3. Səhifənin **aşağısındakı versiya sətrini** qeyd edin (məsələn `1.0.0+9db12e3 · schema 16`).
4. Aşağıdakı formatda bildirin.

Sistem cavab vermirsə və ya səhifə açılmırsa, texniki inzibatçıya deyin — inzibatçı xidmətin vəziyyətini və saxlanmış məlumatı yoxlamalıdır.

## 9. Rəy (feedback) formatı

Hər bildiriş üçün beş sətir kifayətdir:

```
İş №:            2026/0014
Səhifə/əməliyyat: İş səhifəsi → Sənəd yüklə
Nə etmək istəyirdim:  Skan edilmiş razılıq məktubunu tələbə əlavə etmək
Nə baş verdi:     "Ön baxış alınmadı" yazısı çıxdı, sənəd siyahıda göründü
Nə gözləyirdim:   Ön baxışın açılmasını
Versiya:          1.0.0+9db12e3 · schema 16
```

Bildirişi dörd kateqoriyadan birinə aid edin:

| Kateqoriya | Nə deməkdir |
|---|---|
| **Bug** | sistem səhv işləyir, xəta verir |
| **Workflow** | sistem sizin real iş qaydanıza uyğun gəlmir |
| **UI/UX** | anlaşılmır, əlverişsizdir, çox klik tələb edir |
| **Çatışmayan funksiya** | lazım olan imkan ümumiyyətlə yoxdur |

**Axtarış** funksiyası bu pilotda qəsdən yoxdur. Sizə lazım olan axtarış əlamətlərini qeyd edin
(iş nömrəsi, məktub nömrəsi, təşkilat, məsul əməkdaş, status, tarix və s.) — sonrakı mərhələ buna görə qurulacaq.

## 10. Pilot haqqında bilməli olduğunuz məhdudiyyətlər

- Bu, **sınaq mühitidir**: bir istifadəçi, 10 gün, real sənədlərlə.
- Real istifadədən əvvəl inzibatçı gündəlik ehtiyat nüsxəni qurmalı və pilot serverdə bərpanı yoxlamalıdır.
- E-poçt, SMS, bildiriş göndərilmir. Heç bir məlumat internetə çıxmır.
- Sənədlər yalnız RCS vasitəsilə əlçatandır; şəbəkə qovluğu paylaşılmır.
