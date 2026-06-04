<script setup lang="ts">
import { ref, watch, onMounted, onBeforeUnmount, type PropType } from 'vue'
import uPlot from 'uplot'
import 'uplot/dist/uPlot.min.css'

const props = defineProps({
  data: {
    type: Array as PropType<(number | null)[][]>,
    required: true,
  },
  series: {
    type: Array as PropType<uPlot.Series[]>,
    required: true,
  },
  width: {
    type: Number,
    default: 0,
  },
  height: {
    type: Number,
    default: 300,
  },
  title: {
    type: String,
    default: '',
  },
})

const container = ref<HTMLDivElement>()
let chart: uPlot | null = null
let resizeObserver: ResizeObserver | null = null

function buildOpts(): uPlot.Options {
  const w = props.width || container.value?.clientWidth || 800
  return {
    width: w,
    height: props.height,
    title: props.title || undefined,
    cursor: { drag: { x: true, y: false } },
    scales: {
      x: { time: true },
    },
    axes: [
      {
        stroke: '#8892a4',
        grid: { stroke: 'rgba(255,255,255,0.05)' },
      },
      {
        stroke: '#8892a4',
        grid: { stroke: 'rgba(255,255,255,0.05)' },
      },
    ],
    series: [
      {}, // x-axis placeholder
      ...props.series,
    ],
  }
}

function initChart() {
  if (!container.value) return
  destroyChart()
  const opts = buildOpts()
  chart = new uPlot(opts, props.data as uPlot.AlignedData, container.value)
}

function destroyChart() {
  if (chart) {
    chart.destroy()
    chart = null
  }
}

onMounted(() => {
  initChart()

  // Auto-resize
  resizeObserver = new ResizeObserver(() => {
    if (chart && container.value) {
      chart.setSize({
        width: container.value.clientWidth,
        height: props.height,
      })
    }
  })
  if (container.value) resizeObserver.observe(container.value)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  destroyChart()
})

// When data changes, setData (O(1) in uPlot)
watch(
  () => props.data,
  (newData) => {
    if (chart && newData && newData.length > 0 && newData[0].length > 0) {
      chart.setData(newData as uPlot.AlignedData)
    }
  },
)

// When series config changes, recreate chart
watch(
  () => props.series,
  () => initChart(),
  { deep: true },
)
</script>

<template>
  <div ref="container" class="uplot-container"></div>
</template>

<style scoped>
.uplot-container {
  width: 100%;
  min-height: v-bind(height + 'px');
}

.uplot-container :deep(.u-wrap) {
  background: var(--bg-card);
  border-radius: 8px;
}
</style>
