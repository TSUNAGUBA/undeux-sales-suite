/**
 * AIチャット・RAG ナレッジ関連の表示ラベルカタログ（SoT）。
 *
 * ChatDomain（業務チャットの部署）や RagCategory の日本語ラベルは
 * 業務チャット・RAG設定・業態部門マスタの複数画面で共有するため、本ファイルに一元定義する。
 * コード値そのものの SoT はバックエンド（API 契約）で、本ファイルは表示射影のみを持つ。
 */
import type { ChatDomain, RagCategory, RagIndexState, RagScope } from '~/types/api'

/** ChatDomain → 担当部署名。業務チャットの窓口カード・ナレッジの対象部署バッジで使う。 */
export const CHAT_DOMAIN_LABELS: Record<ChatDomain, string> = {
  system: 'システム部',
  quality: '商品管理部',
  logistics: '物流部',
}

/** ChatDomain の定義順（選択肢の並び）。 */
export const CHAT_DOMAINS: ChatDomain[] = ['system', 'quality', 'logistics']

/** ChatDomain の表示ラベル（null は「共通」＝部署絞込なし）。 */
export function chatDomainLabel(domain: ChatDomain | null): string {
  return domain ? CHAT_DOMAIN_LABELS[domain] : '共通'
}

/** RagCategory → 表示ラベル。 */
export const RAG_CATEGORY_LABELS: Record<RagCategory, string> = {
  group: 'しまむらグループ',
  business_type: '業態',
  department: '部門',
  basic: '基本情報',
  manual: 'マニュアル',
}

/** スコープごとに選択・表示できるカテゴリ（manual は operator 専用）。 */
export function ragCategoriesForScope(scope: RagScope): RagCategory[] {
  return scope === 'operator'
    ? ['group', 'business_type', 'department', 'basic', 'manual']
    : ['group', 'business_type', 'department', 'basic']
}

/** インデックス状態の表示メタ（ラベル・バッジ配色）。 */
export const RAG_INDEX_STATE_META: Record<RagIndexState, { label: string; badgeClass: string }> = {
  indexed: { label: 'インデックス済', badgeClass: 'border-emerald-200 bg-emerald-50 text-emerald-700' },
  pending: { label: 'インデックス待ち', badgeClass: 'border-slate-200 bg-slate-100 text-slate-500' },
  failed: { label: 'インデックス失敗', badgeClass: 'border-red-200 bg-red-50 text-red-700' },
}

/** ファイル登録で受け付ける拡張子（input accept・クライアント検証の SoT。バックエンド検証と一致させる）。 */
export const RAG_FILE_EXTENSIONS = ['txt', 'md', 'pdf', 'jpg', 'jpeg', 'png'] as const

/** input[type=file] の accept 属性値。 */
export const RAG_FILE_ACCEPT = RAG_FILE_EXTENSIONS.map((ext) => `.${ext}`).join(',')

/** ファイルサイズ上限（20MB。超過はバックエンドで 413 UNDX-IMP-005）。 */
export const RAG_MAX_FILE_BYTES = 20 * 1024 * 1024

/**
 * 画像ファイルのサイズ上限（5MB。バックエンド検証と一致させる）。
 * AI 画像説明（Anthropic API）の画像上限に合わせ、常に失敗する登録を入口で防ぐ。
 */
export const RAG_MAX_IMAGE_BYTES = 5 * 1024 * 1024

/** ファイル名が画像（.jpg/.jpeg/.png）か。 */
export function isRagImageFileName(fileName: string): boolean {
  const ext = fileName.split('.').pop()?.toLowerCase() ?? ''
  return ext === 'jpg' || ext === 'jpeg' || ext === 'png'
}

/** ファイル名の拡張子が許可リストに含まれるか。 */
export function isAllowedRagFileName(fileName: string): boolean {
  const ext = fileName.split('.').pop()?.toLowerCase() ?? ''
  return (RAG_FILE_EXTENSIONS as readonly string[]).includes(ext)
}

/** ファイルサイズの表示（B / KB / MB）。 */
export function formatFileSize(bytes: number | null | undefined): string {
  if (bytes == null) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
