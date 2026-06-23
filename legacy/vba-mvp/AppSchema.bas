Attribute VB_Name = "AppSchema"
Option Explicit
'===============================================================================
' Layer:    Global
' Name:     Schema
' Purpose:  定義 DbSchema 使用的使用者自訂類型 (User-Defined Type)
'===============================================================================

'-------------------------------------------------------------------------------
' Source Layer
'-------------------------------------------------------------------------------
' [GL] -屬於動態映射

' [TB] -屬於動態映射

' [科目配對表]
Public Type TypeSourceAccountMapping
    Name As String                      ' 資料表名稱
    AccountCode As String               ' 科目編號
    AccountName As String               ' 科目名稱
    AccountStandardized As String       ' 科目分類名稱
End Type

' [日期維度表]
Public Type TypeSourceDateDimension
    Name As String                      ' 資料表名稱
    DateKey As String                   '[INTEGER]  唯一主鍵，格式: YYYYMMDD
    FullDate As String                  '[DATE]     日期欄位
    Year As String                      '[NUM]      西元年 (1900-?)
    Month As String                     '[NUM]      月份 (1-12)
    Day As String                       '[NUM]      日 (1-31)
    DayOfWeek As String                 '[NUM]      天 (1-7)
    IsWeekend As String                 '[Y/N]      -1/0 (系統自動產生)
    IsHolidays As String                '[Y/N]      -1/0 (由使用者上傳的資料覆寫)
    IsMakeupDays As String              '[Y/N]      -1/0 (由使用者上傳的資料覆寫)
    HolidaysDesc As String              '[CHAR]     假期說明 (選填)
    MakeupDaysDesc As String            '[CHAR]     補班日說明 (選填)
End Type

'-------------------------------------------------------------------------------
' Staging Layer
'-------------------------------------------------------------------------------
Public Type TypeEtlJeInPeriod
    Name As String
End Type

Public Type TypeEtlJeNotInPeriod
    Name As String
End Type

Public Type TypeEtlAccountSum
    Name As String
End Type

Public Type TypeEtlCompletenessCal
    Name As String
    AccountMerged As String ' K_ACCOUNT_MERGED
    Diff As String          ' K_TB_JE_DIFF
End Type

Public Type TypeEtlCompletenessDiff
    Name As String
End Type

Public Type TypeEtlCompletenessDetail
    Name As String
End Type

Public Type TypeEtlDocBalanceSum
    Name As String
End Type

Public Type TypeEtlDocBalanceDiff
    Name As String
End Type

Public Type TypeEtlDocBalanceDetail
    Name As String
End Type

Public Type TypeEtlInfRandom
    Name As String
End Type

Public Type TypeEtlInfSorted
    Name As String
End Type

Public Type TypeEtlNullAccount
    Name As String
End Type

Public Type TypeEtlNullDoc
    Name As String
End Type

Public Type TypeEtlNullDesc
    Name As String
End Type

'-------------------------------------------------------------------------------
'-- Target Layer
'-------------------------------------------------------------------------------
Public Type TypeTargetEngagementOverview
    Name As String
    Client As String                    ' 客戶
    PrepStartDate As String             ' 財報準備期間開始日
    PeriodStart As String               ' 資料期間開始日
    PeriodEnd As String                 ' 資料期間結束日
    PreparedBy As String                ' 報表生產人
    PreparedDate As String              ' 報表生產時間
End Type

Public Type TypeTargetDataOverview
    Name As String
    JeName As String                    ' 分錄檔案名稱
    JeNetAmount As String               ' 分錄借貸淨額
    JeDebitSum As String                ' 分錄借方合計金額
    JeCreditSum As String               ' 分錄貸方合計金額
    JeRecordCount As String             ' 分錄筆數
    TbName As String                    ' 試算表檔案名稱
    TbAccountCount As String            ' 試算表科目數量
End Type

Public Type TypeTargetValidationOverview
    Name As String
    NullAccountRecordCount As String    ' 無會計科目編號筆數
    NullDocumentRecordCount As String   ' 無傳票號碼筆數
    NullDescriptionRecordCount As String ' 無傳票摘要筆數
    NotInPeriodCount As String          ' 傳票核准日不在會計期間內筆數
    CompletenessDiffCount As String     ' 完整性差異筆數
    DocumentBalanceDiffCount As String  ' 借貸不平差異筆數
End Type

Public Type TypeTargetCompletenessDetail
    Name As String
    '-- 此報表直接複製 COMPLETENESS_DETAIL 表
End Type

Public Type TypeTargetDocumentBalanceDetail
    Name As String
    '-- 此報表直接複製 DOCUMENT_BALANCE_DETAIL 表
End Type

Public Type TypeTargetInfSampleDetail
    Name As String
    '-- 此報表直接複製 INF_SORTED 表
End Type

Public Type TypeTargetAccountMappingInfo
    Name As String
End Type

Public Type TypeTargetFieldMappingInfo
    Name As String
End Type



