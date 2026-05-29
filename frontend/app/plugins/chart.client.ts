import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  BarElement,
  BarController,
  LineController,
  Title,
  Tooltip,
  Legend,
  Filler,
} from 'chart.js'

// Chart.js の利用コンポーネントを登録する。
// BarController / LineController を明示登録するのは、ランキング分析のパレート図が
// 棒（構成比）と折れ線（累積構成比）を1つのチャートに混在させる複合チャートのため。
// 単体の Bar/Line だけなら vue-chartjs が各コントローラを登録するが、混在チャートでは
// マウント順に依存せず両コントローラが必要になるため、プラグインで一括登録して保証する。
export default defineNuxtPlugin(() => {
  ChartJS.register(
    CategoryScale,
    LinearScale,
    PointElement,
    LineElement,
    BarElement,
    BarController,
    LineController,
    Title,
    Tooltip,
    Legend,
    Filler,
  )
})
