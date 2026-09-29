import { Component, ChangeDetectorRef } from '@angular/core';
import { finalize } from 'rxjs';
import {
  ExcelToTallyService,
  ExcelValidationResult,
  ValidatedExcelTransaction,
  ExcelValidationSeverity
} from '../../services/excel-to-tally.service';

type FilterTab = 'ALL' | 'VALID' | 'WARNING' | 'INVALID';

@Component({
  selector: 'app-excel-to-tally',
  templateUrl: './excel-to-tally.component.html',
  styleUrls: ['./excel-to-tally.component.css'],
  standalone: false
})
export class ExcelToTallyComponent {
  selectedFile: File | null = null;
  isValidating = false;
  isDownloadingTemplate = false;
  isGeneratingXml = false;
  errorMessage: string | null = null;
  successMessage: string | null = null;
  xmlSuccessMessage: string | null = null;
  xmlErrorMessage: string | null = null;

  validationResult: ExcelValidationResult | null = null;
  activeFilter: FilterTab = 'ALL';
  searchTerm = '';
  templateDownloaded = false;

  get step1Status(): 'completed' | 'active' | 'pending' {
    if (this.templateDownloaded || this.selectedFile || this.validationResult) return 'completed';
    return 'active';
  }

  get step2Status(): 'completed' | 'active' | 'pending' {
    if (this.selectedFile || this.validationResult) return 'completed';
    if (this.templateDownloaded) return 'active';
    return 'pending';
  }

  get step3Status(): 'completed' | 'active' | 'pending' | 'error' {
    if (this.isValidating) return 'active';
    if (this.validationResult) {
      if (!this.validationResult.isReadyForXmlGeneration) return 'error';
      return 'completed';
    }
    if (this.selectedFile) return 'active';
    return 'pending';
  }

  get step4Status(): 'completed' | 'active' | 'pending' {
    if (this.validationResult) return 'completed';
    return 'pending';
  }

  get step5Status(): 'completed' | 'active' | 'pending' {
    if (this.xmlSuccessMessage) return 'completed';
    if (this.isGeneratingXml) return 'active';
    if (this.validationResult?.isReadyForXmlGeneration) return 'active';
    return 'pending';
  }

  get step6Status(): 'completed' | 'active' | 'pending' {
    if (this.xmlSuccessMessage) return 'completed';
    return 'pending';
  }

  get fileStatusBadge(): { text: string; cssClass: string } {
    if (this.isValidating) return { text: 'Validating', cssClass: 'badge-validating' };
    if (this.errorMessage && !this.validationResult) return { text: 'Failed', cssClass: 'badge-failed' };
    if (!this.validationResult) return { text: 'Uploaded', cssClass: 'badge-uploaded' };
    if (this.xmlSuccessMessage) return { text: 'XML Ready', cssClass: 'badge-success' };
    if (this.isGeneratingXml) return { text: 'Generating XML', cssClass: 'badge-generating' };
    if (this.validationResult.isReadyForXmlGeneration) return { text: 'Ready for XML', cssClass: 'badge-ready' };
    if (this.validationResult.invalidRows > 0) return { text: 'Invalid', cssClass: 'badge-invalid' };
    if (this.validationResult.warningRows > 0) return { text: 'Review Required', cssClass: 'badge-warning' };
    return { text: 'Valid', cssClass: 'badge-valid' };
  }

  formatFileSize(bytes: number | undefined): string {
    if (!bytes || bytes <= 0) return '0 B';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  }

  formatDate(dateVal: any): string {
    if (!dateVal) return '—';
    try {
      if (typeof dateVal === 'string' && /^\d{2}\/\d{2}\/\d{4}$/.test(dateVal)) {
        return dateVal;
      }
      const d = new Date(dateVal);
      if (isNaN(d.getTime())) return String(dateVal);
      const day = String(d.getDate()).padStart(2, '0');
      const month = String(d.getMonth() + 1).padStart(2, '0');
      const year = d.getFullYear();
      return `${day}/${month}/${year}`;
    } catch {
      return String(dateVal || '—');
    }
  }

  constructor(
    private readonly excelService: ExcelToTallyService,
    private readonly cdr: ChangeDetectorRef
  ) {}

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.handleFile(input.files[0]);
    }
    input.value = '';
    this.cdr.markForCheck();
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    event.stopPropagation();
    if (event.dataTransfer?.files && event.dataTransfer.files.length > 0) {
      this.handleFile(event.dataTransfer.files[0]);
    }
  }

  handleFile(file: File): void {
    this.errorMessage = null;
    this.successMessage = null;
    this.xmlSuccessMessage = null;
    this.xmlErrorMessage = null;
    this.validationResult = null;

    const ext = file.name.split('.').pop()?.toLowerCase();
    if (ext !== 'xlsm' && ext !== 'xlsx' && ext !== 'xls') {
      this.errorMessage = `Invalid file format '.${ext}'. Please upload the official ACCUFEX Excel template (.xlsm) or .xlsx.`;
      this.cdr.markForCheck();
      return;
    }

    if (file.size > 50 * 1024 * 1024) {
      this.errorMessage = 'File size exceeds the 50MB limit.';
      this.cdr.markForCheck();
      return;
    }

    this.selectedFile = file;
    this.cdr.markForCheck();
    this.validateUploadedFile();
  }

  validateUploadedFile(): void {
    if (!this.selectedFile) return;

    this.isValidating = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.excelService.validateExcelFile(this.selectedFile)
      .pipe(
        finalize(() => {
          this.isValidating = false;
          this.cdr.markForCheck();
        })
      )
      .subscribe({
        next: (res) => {
          this.validationResult = res;

          if (!res.success && res.errorMessage) {
            this.errorMessage = res.errorMessage;
          }
          this.cdr.markForCheck();
        },
        error: (err) => {
          console.error('[ExcelToTally] Received validation error response:', err);
          if (err.error && typeof err.error === 'object' && err.error.errorMessage) {
            this.validationResult = err.error as ExcelValidationResult;
            this.errorMessage = err.error.errorMessage;
          } else {
            this.errorMessage = err.message || 'An unexpected error occurred while validating the Excel file.';
          }
          this.cdr.markForCheck();
        }
      });
  }

  downloadTemplate(): void {
    this.isDownloadingTemplate = true;
    this.errorMessage = null;
    this.cdr.markForCheck();

    this.excelService.downloadTemplate()
      .pipe(
        finalize(() => {
          this.isDownloadingTemplate = false;
          this.cdr.markForCheck();
        })
      )
      .subscribe({
        next: (blob) => {
          this.templateDownloaded = true;
          this.excelService.saveBlob(blob, 'EasyFin_Tally_Import_Template_v1.xlsm');
          this.successMessage = 'Official ACCUFEX .XLSM template downloaded successfully.';
          this.cdr.markForCheck();
          setTimeout(() => {
            this.successMessage = null;
            this.cdr.markForCheck();
          }, 4000);
        },
        error: (err) => {
          console.error('[ExcelToTally] Failed to download template:', err);
          this.errorMessage = 'Failed to download Excel template. Please try again.';
          this.cdr.markForCheck();
        }
      });
  }

  setFilter(filter: FilterTab): void {
    this.activeFilter = filter;
    this.cdr.markForCheck();
  }

  isFatal(severity: any): boolean {
    return severity === ExcelValidationSeverity.Fatal || severity === 2 || severity === 'Fatal';
  }

  isWarning(severity: any): boolean {
    return severity === ExcelValidationSeverity.Warning || severity === 1 || severity === 'Warning';
  }

  get filteredTransactions(): ValidatedExcelTransaction[] {
    if (!this.validationResult) return [];

    let list = this.validationResult.parsedTransactions || [];

    if (this.activeFilter === 'VALID') {
      list = list.filter((t) => t.status === 'Valid');
    } else if (this.activeFilter === 'WARNING') {
      list = list.filter((t) => t.status === 'Warning');
    } else if (this.activeFilter === 'INVALID') {
      list = list.filter((t) => t.status === 'Invalid');
    }

    if (this.searchTerm.trim()) {
      const q = this.searchTerm.toLowerCase();
      list = list.filter(
        (t) =>
          (t.narration && t.narration.toLowerCase().includes(q)) ||
          (t.ledgerName && t.ledgerName.toLowerCase().includes(q)) ||
          (t.bankName && t.bankName.toLowerCase().includes(q)) ||
          (t.chequeRefNo && t.chequeRefNo.toLowerCase().includes(q))
      );
    }

    return list;
  }

  generateTallyXml(): void {
    if (!this.validationResult || !this.validationResult.isReadyForXmlGeneration) {
      this.xmlErrorMessage = 'Cannot generate XML: Workbook contains fatal validation errors or is not ready.';
      this.cdr.markForCheck();
      return;
    }

    if (this.isGeneratingXml) {
      return; // Duplicate click protection
    }

    this.isGeneratingXml = true;
    this.xmlErrorMessage = null;
    this.xmlSuccessMessage = null;
    this.cdr.markForCheck();

    this.excelService.generateTallyXml(this.validationResult)
      .pipe(
        finalize(() => {
          this.isGeneratingXml = false;
          this.cdr.markForCheck();
        })
      )
      .subscribe({
        next: (blob) => {
          const now = new Date();
          const pad = (n: number) => n.toString().padStart(2, '0');
          const timestamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}_${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
          const fileName = `EasyFin_Tally_Export_${timestamp}.xml`;
          this.excelService.saveBlob(blob, fileName);

          this.xmlSuccessMessage = 'Tally XML generated successfully.';
          this.cdr.markForCheck();
        },
        error: async (err) => {
          console.error('[ExcelToTally] Failed to generate XML:', err);
          let errorText = 'XML generation failed due to a server error. No XML file was generated.';

          if (err.error instanceof Blob) {
            try {
              const text = await err.error.text();
              const json = JSON.parse(text);
              if (json?.message) {
                errorText = json.message;
              }
            } catch {
              // fallback to default
            }
          } else if (err.error?.message) {
            errorText = err.error.message;
          } else if (err.message) {
            errorText = err.message;
          }

          this.xmlErrorMessage = errorText;
          this.cdr.markForCheck();
        }
      });
  }

  reset(): void {
    this.selectedFile = null;
    this.validationResult = null;
    this.errorMessage = null;
    this.successMessage = null;
    this.xmlSuccessMessage = null;
    this.xmlErrorMessage = null;
    this.activeFilter = 'ALL';
    this.searchTerm = '';
    this.isValidating = false;
    this.isGeneratingXml = false;
    this.templateDownloaded = false;
    this.cdr.markForCheck();
  }
}
