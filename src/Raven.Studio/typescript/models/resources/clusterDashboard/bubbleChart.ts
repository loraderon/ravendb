/// <reference path="../../../../typings/tsd.d.ts"/>

import d3 = require("d3");
import moment = require("moment");
import { clusterDashboardChart } from "models/resources/clusterDashboard/clusterDashboardChart";


interface chartItemData {
    parent: chartData;
    x: Date;
    y: number;
}

export interface chartData {
    id: string;
    values: chartItemData[];
}

type chartOpts<TPayload extends { Date: string }, TExtra> = {
    grid?: boolean;
    yMaxProvider?: (data: chartData[]) => number | null;
    useSeparateYScales?: boolean;
    topPaddingProvider?: (key: string) => number;
    tooltipProvider?: (unalignedDate: ClusterWidgetUnalignedDate|null) => string;
    onMouseMove?: (date: ClusterWidgetUnalignedDate|null) => void;
    extraArgumentsProvider?: (payload: TPayload) => TExtra;
    drawPoints?: (selection: d3.Selection<chartItemData & TExtra>) => void;
}

export class bubbleChart<TPayload extends { Date: string }, TExtra = unknown> implements clusterDashboardChart<TPayload> {
    
    static readonly defaultTopPadding = 5;
    static readonly timeFormat = "h:mm:ss A";
    
    private width: number;
    private height: number;
    
    private minDate: ClusterWidgetUnalignedDate = null;
    private maxDate: ClusterWidgetUnalignedDate = null;
    private data: chartData[] = [];
    private opts: chartOpts<TPayload, TExtra>;
    
    private svg: d3.Selection<void>;
    private pointer: d3.Selection<void>;
    private tooltip: d3.Selection<void>;
    
    private xScale: d3.time.Scale<number, number>;
    
    private readonly containerSelector: string | EventTarget;
    private highlightDate: Date;
    private readonly dataProvider: (payload: TPayload) => number;
    private readonly drawPoints: (selection: d3.Selection<any>) => void;
    
    constructor(containerSelector: string | EventTarget, dataProvider: (payload: TPayload) => number, opts?: chartOpts<TPayload, TExtra>) {
        this.opts = opts || {} as any;
        this.dataProvider = dataProvider;
        this.containerSelector = containerSelector;
        
        if (!this.opts.topPaddingProvider) {
            this.opts.topPaddingProvider = () => bubbleChart.defaultTopPadding;
        }
        
        const container = d3.select(containerSelector as string);
        
        const $container = $(containerSelector);
        
        this.width = $container.innerWidth();
        this.height = $container.innerHeight();

        this.drawPoints = opts.drawPoints ?? ((selection: d3.Selection<chartItemData & TExtra>) => selection.append("circle").attr("r", 8));

        this.svg = container
            .append("svg")
            .attr("width", this.width)
            .attr("height", this.height);
        
        if (this.opts.grid) {
            const gridContainer = this.svg
                .append("g")
                .attr("transform", "translate(-0.5, 0)")
                .attr("class", "grid");
            this.drawGrid(gridContainer);
        }
        
        this.svg
            .append("g")
            .attr("class", "series");
        
        const pointer = this.svg
            .append("g")
            .attr("class", "pointer");
        
        this.pointer = pointer.append("line")
            .attr("class", "pointer-line")
            .attr("x1", 0)
            .attr("x2", 0)
            .attr("y1", 0)
            .attr("y2", this.height)
            .style("stroke-opacity", 0);
        
        this.tooltip = d3.select(".cluster-dashboard-tooltip");
        
        if (this.opts.tooltipProvider || this.opts.onMouseMove) {
            this.setupValuesPreview();
        }
    }
    
    private drawGrid(gridContainer: d3.Selection<any>) {
        const gridLocation = _.range(0, this.width, 40)
            .map(x => this.width - x);
        
        const lines = gridContainer.selectAll("line")
            .data(gridLocation);
        
        lines
            .exit()
            .remove();
        
        lines
            .enter()
            .append("line")
            .attr("class", "grid-line")
            .attr("x1", x => x)
            .attr("x2", x => x)
            .attr("y1", 0)
            .attr("y2", this.height);
    }
    
    onResize() {
        const container = d3.select(this.containerSelector as string);
        
        const $container = $(this.containerSelector);
        
        this.width = $container.innerWidth();
        this.height = $container.innerHeight();

        this.svg = container
            .select("svg")
            .attr("width", this.width)
            .attr("height", this.height);
        
        const gridContainer = this.svg.select(".grid");
        gridContainer.selectAll("line").remove();
        
        this.drawGrid(gridContainer);
    }
    
    highlightTime(date: ClusterWidgetAlignedDate|null) {
        if (date) {
            const xToHighlight = this.xScale(date);
            if (xToHighlight != null) {
                if (!this.highlightDate) {
                    this.pointer
                        .transition()
                        .duration(200)
                        .style("stroke-opacity", 1);
                }
                this.highlightDate = date;

                this.pointer
                    .attr("x1", xToHighlight + 0.5)
                    .attr("x2", xToHighlight + 0.5);
                
                return;
            }
        } 
        
        // remove highlight - no date or no snap
        this.pointer
            .transition()
            .duration(100)
            .style("stroke-opacity", 0);
        this.highlightDate = null;
    }
    
    private setupValuesPreview() {
        const withTooltip = !!this.opts.tooltipProvider;
        this.svg
            .on("mousemove.tip", () => {
                if (this.xScale) {
                    const node = this.svg.node();
                    const mouseLocation = d3.mouse(node);

                    const hoverTime = this.xScale.invert(mouseLocation[0]);
                    this.opts?.onMouseMove(hoverTime);

                    if (withTooltip) {
                        this.updateTooltip();
                    }
                }
            })
            .on("mouseenter.tip", () => {
                if (withTooltip) {
                    this.showTooltip();
                }
            })
            .on("mouseleave.tip", () => {
                if (withTooltip) {
                    this.hideTooltip();
                }
                
                this.opts?.onMouseMove(null);
            });
    }
    
    showTooltip() {
        this.tooltip
            .style("display", undefined)
            .transition()
            .duration(250)
            .style("opacity", 1);
    }
    
    updateTooltip() {
        const xToUse = d3.mouse(this.svg.node())[0];
        this.tooltip.style('display', undefined);
        
        if (!_.isNull(xToUse) && this.minDate) {
            const date = this.findDate(xToUse);
            const html = this.opts.tooltipProvider(date) || "";
            
            if (html) {
                this.tooltip.html(html);
                this.tooltip.style("display", undefined);
            } else {
                this.tooltip.style("display", "none");
            }
        }
        
        const container = d3.select(".cluster-dashboard-container").node();
        const globalLocation = d3.mouse(container);
        const [x, y] = globalLocation;
        
        const tooltipWidth = $(this.tooltip.node()).width();
        const containerWidth = $(container).innerWidth();
        
        const tooltipX = Math.min(x + 10, containerWidth - tooltipWidth);
        
        this.tooltip
            .style("left", tooltipX + "px")
            .style("top", (y + 10) + "px")
    }
    
    private findDate(xToUse: number): Date {
        // noinspection JSSuspiciousNameCombination
        const hoverTime = this.xScale.invert(xToUse);
        return hoverTime.getTime() >= this.minDate.getTime() ? hoverTime : null; 
    }
    
    hideTooltip() {
        this.tooltip.transition()
            .duration(250)
            .style("opacity", 0)
            .each('end', () => this.tooltip.style('display', 'none'));
    }
    
    onHeartbeat(date: ClusterWidgetUnalignedDate) {
        this.maxDate = date;
    }
    
    onData(key: string, payload: TPayload) {
        const transformed = this.dataProvider(payload);
        if (typeof transformed === "undefined") {
            return;
        }
        const date = moment.utc(payload.Date).toDate();
        const extra = this.opts.extraArgumentsProvider?.(payload);
        this.onDataInternal(date, key, transformed, extra);
        
    }
    private onDataInternal(time: ClusterWidgetUnalignedDate, key: string, value: number, extra: TExtra = null) {
        if (!this.minDate) {
            this.minDate = time;
        }
        this.maxDate = time;
        

        const data = this.getOrCreateChartData(key); 

        data.values.push({
            x: time,
            y: value,
            parent: data,
            ...extra
        });
        
        this.maybeTrimData();
    }
    
    private getOrCreateChartData(key: string): chartData {
        let dataEntry = this.data.find(x => x.id === key);

        if (!dataEntry) {
            dataEntry = {
                id: key,
                values: []
            };
            this.data.push(dataEntry);
        }
        
        return dataEntry;
    }
    
    recordNoData() { 
        // no-op here
    }
    
    private maybeTrimData() {
        const hasAnyTrim = false;
        
        for (const datum of this.data) {
            if (datum.values.length > 2000) {
                datum.values = datum.values.slice(datum.values.length - 1500);
            }
        }

        if (hasAnyTrim) {
            this.minDate = _.min(this.data.filter(x => x.values.length).map(d => d.values[0].x));
        }
    }
    
    private createXScale() {
        const timePerPixel = 500;
        const maxTime = this.maxDate;
        if (!maxTime) {
            return null;
        }
        const minTime = new Date(maxTime.getTime() - this.width * timePerPixel);
        return d3.time.scale()
            .range([0, this.width])
            .domain([minTime, maxTime]);
    }
    
    private createYScales(): Map<string, d3.scale.Linear<number, number>> {
        if (!this.data.length) {
            return new Map<string, d3.scale.Linear<number, number>>();
        }
        
        const result = new Map<string, d3.scale.Linear<number, number>>();
        
        const yScaleCreator = (maxValue: number, topPadding: number) => {
            if (!maxValue) {
                maxValue = 1;
            }
            return d3.scale.linear()
                .range([topPadding != null ? topPadding : bubbleChart.defaultTopPadding, this.height])
                .domain([maxValue, 0]);
        };
        
        if (this.opts.yMaxProvider != null) {
            const yScale = yScaleCreator(this.opts.yMaxProvider(this.data), this.opts.topPaddingProvider(null));
            
            this.data.forEach(data => {
                result.set(data.id, yScale);
            });
        } else if (this.opts.useSeparateYScales) {
            this.data.filter(x => x.values.length).forEach(data => {
                const yMax = d3.max(data.values.map(values => values.y));
                const yScale = yScaleCreator(yMax, this.opts.topPaddingProvider(data.id));
                
                result.set(data.id, yScale);
            });
        } else {
            const yMax = d3.max(this.data.filter(x => x.values.length).map(data => d3.max(data.values.map(values => values.y))));
            const yScale = yScaleCreator(yMax, this.opts.topPaddingProvider(null));

            this.data.forEach(data => {
                result.set(data.id, yScale);
            });
        }
     
        return result;
    }
    
    draw() {
        const series = this.svg
            .select(".series")
            .selectAll(".serie")
            .data(this.data, x => x.id);
        
        series
            .exit()
            .remove();
        
        series
            .enter()
            .append("g")
            .attr("class", x => "serie " + x.id);
        
        this.xScale = this.createXScale();
        const yScales = this.createYScales();
        
        const points = series.selectAll(".point")
            .data<chartItemData>(x => x.values, x => x.x.getTime().toString());
        
        points
            .exit()
            .remove();
        
        const incomingGroups = points.enter()
            .append("g")
            .attr("class", "point");
        
        incomingGroups.call(this.drawPoints);
        
        points
            .attr("transform", d => "translate(" + (this.xScale(d.x)) + "," + (yScales.get(d.parent.id)(d.y)) + ")");
        
        if (this.highlightDate) {
            this.highlightTime(this.highlightDate);
        }
    }
}
