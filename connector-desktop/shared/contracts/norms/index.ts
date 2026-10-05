// Единый контракт машиночитаемого нормативного документа (СП/ГОСТ/СНиП).
// Зеркало norm-document.schema.json. Источник истины — JSON Schema.
//
// Принцип: храним ФУНКЦИОНАЛЬНОЕ содержание (параметры, формулы, таблицы,
// правила, термины, ссылки на пункты + краткие пересказы), НЕ дословный полный
// текст. `structure` — дерево пунктов; `entities` — плоский id-адресуемый пул,
// который читает расчётный слой (checks/) и рендерит фронт.

export type Material =
  | 'reinforced_concrete' | 'concrete' | 'steel' | 'composite' | 'soil' | 'any'
export type LimitState = 'ULS' | 'SLS' | 'fatigue' | 'any'
export type ActionKind =
  | 'bending' | 'shear' | 'axial_compression' | 'axial_tension'
  | 'eccentric_compression' | 'torsion' | 'biaxial_bending'
  | 'crack_width' | 'deflection' | 'stability'
export type Confidence = 'high' | 'medium' | 'low'

export interface Applicability {
  material?: Material
  sectionTypes?: string[]            // rectangular | tee | box | circular | ring | i_beam | generic …
  limitState?: LimitState
  action?: ActionKind[]
  conditions?: string[]              // доп. условия как выражения: 'x2 >= 2*a2'
  notes?: string
}

export interface SourceRef {
  document?: string                  // код документа, если отличается от родителя
  clause: string                     // '7.62' | '8.1.8'
  subitem?: string
  url?: string
}

export interface EntityBase {
  id: string                         // 'formula:M_ult_rect_sp35_7_62'
  source: SourceRef
  verified?: boolean                 // сверено экспертом с официальным источником
  confidence?: Confidence
  notes?: string
}

export interface Clause {
  id: string
  number: string                     // '7' | '7.62' | '8.1.8'
  title: string
  summary?: string                   // КРАТКИЙ пересказ (не дословный текст СП)
  parent?: string | null
  children?: string[]
  introduces?: string[]              // id вводимых сущностей
}

export type ParamKind =
  | 'material_property' | 'geometry' | 'force' | 'coefficient' | 'result' | 'derived' | 'other'

export interface Parameter extends EntityBase {
  symbol: string                     // 'Rb' | 'h0' | 'xi_y'
  displaySymbol?: string             // 'R_b' | 'ξ_y'
  name: string
  unit: string | null                // 'MPa' | 'mm' | 'N' | 'N*mm' | null
  kind?: ParamKind
}

export interface Formula extends EntityBase {
  title?: string
  result: string                     // symbol результата
  expression: string                 // вычислимое выражение
  variables: string[]
  units?: Record<string, string>
  applicability: Applicability
  latex?: string
}

export interface TableColumn {
  id: string
  name: string
  symbol?: string
  unit?: string | null
}

export interface NormTable extends EntityBase {
  title: string
  keyColumns?: string[]
  columns: TableColumn[]
  rows: Record<string, unknown>[]
  applicability?: Applicability
}

export interface Rule extends EntityBase {
  name: string
  predicate: string                  // 'xi <= xi_y' | 'M <= M_ult'
  demand?: string                    // числитель ratio
  capacity?: string                  // знаменатель ratio
  onFail?: string
  refsFormulas?: string[]
  applicability: Applicability
}

export interface Coefficient extends EntityBase {
  symbol: string
  name: string
  value?: number | null
  lookupTable?: string               // id таблицы, если зависит от условий
  unit?: string | null
}

export interface Term extends EntityBase {
  term: string
  definition: string
}

export interface NormDocumentMeta {
  code: string                       // 'СП 35.13330.2011'
  title: string
  edition?: string
  status: 'active' | 'superseded' | 'draft'
  supersedes?: string
  approvedBy?: string
  approvedDate?: string
  officialSourceUrl?: string
  lang: string
}

export interface NormProvenance {
  extractedBy?: string
  extractedAt?: string
  sources?: string[]
  verified: boolean
  verifiedBy?: string
  notes?: string
}

export interface NormEntities {
  parameters?: Parameter[]
  formulas?: Formula[]
  tables?: NormTable[]
  rules?: Rule[]
  coefficients?: Coefficient[]
  terms?: Term[]
}

export interface NormDocument {
  schemaVersion: '1.0'
  document: NormDocumentMeta
  structure: Clause[]
  entities: NormEntities
  provenance: NormProvenance
}
