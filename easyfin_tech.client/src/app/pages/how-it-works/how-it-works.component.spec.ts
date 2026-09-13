import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { NO_ERRORS_SCHEMA, provideZonelessChangeDetection } from '@angular/core';
import { RouterModule } from '@angular/router';
import { By } from '@angular/platform-browser';
import { Observable, of } from 'rxjs';
import { describe, it, expect, beforeEach } from 'vitest';
import { HowItWorksComponent } from './how-it-works.component';
import { AuthService } from '../../services/auth.service';

describe('HowItWorksComponent (Phase 14 UI Redesign)', () => {
  let component: HowItWorksComponent;
  let fixture: ComponentFixture<HowItWorksComponent>;
  let mockAuthService: {
    isAuthenticated: boolean;
    currentUser$: Observable<unknown>;
  };

  beforeEach(async () => {
    mockAuthService = {
      isAuthenticated: true,
      currentUser$: of(null)
    };

    await TestBed.configureTestingModule({
      declarations: [HowItWorksComponent],
      imports: [CommonModule, RouterModule.forRoot([])],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuthService, useValue: mockAuthService }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(HowItWorksComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('1. should create HowItWorksComponent with default active workflow', () => {
    expect(component).toBeTruthy();
    expect(component.activeWorkflow).toBe('bank-to-excel');
    expect(component.isAuthenticated).toBe(true);
  });

  it('2. should render main headline with factual value proposition', () => {
    const titleEl = fixture.debugElement.query(By.css('#howItWorksMainTitle'));
    expect(titleEl).toBeTruthy();
    expect(titleEl.nativeElement.textContent).toContain('From financial data to structured output.');
  });

  it('3. should render 3 distinct workflow selector cards', () => {
    const tabWf1 = fixture.debugElement.query(By.css('#tabBankToExcel'));
    const tabWf2 = fixture.debugElement.query(By.css('#tabExcelToTally'));
    const tabWf3 = fixture.debugElement.query(By.css('#tabBillsInvoices'));

    expect(tabWf1).toBeTruthy();
    expect(tabWf2).toBeTruthy();
    expect(tabWf3).toBeTruthy();

    expect(tabWf1.nativeElement.textContent).toContain('Bank Statement PDF → Excel');
    expect(tabWf2.nativeElement.textContent).toContain('Excel → Tally XML');
    expect(tabWf3.nativeElement.textContent).toContain('Bills & Invoices → Excel');
  });

  it('4. should display 6 sequential steps for Workflow 1 (Bank Statement to Excel)', () => {
    component.setWorkflow('bank-to-excel');
    fixture.detectChanges();

    const wf1Section = fixture.debugElement.query(By.css('#sectionBankToExcel'));
    expect(wf1Section).toBeTruthy();

    const steps = wf1Section.queryAll(By.css('.timeline-step'));
    expect(steps.length).toBe(6);

    const converterBtn = wf1Section.query(By.css('#hiwConverterCta'));
    expect(converterBtn).toBeTruthy();
    expect(converterBtn.attributes['routerLink']).toBe('/converter');
  });

  it('5. should display 7 sequential steps for Workflow 2 (Excel to Tally XML)', () => {
    component.setWorkflow('excel-to-tally');
    fixture.detectChanges();

    const wf2Section = fixture.debugElement.query(By.css('#sectionExcelToTally'));
    expect(wf2Section).toBeTruthy();

    const steps = wf2Section.queryAll(By.css('.timeline-step'));
    expect(steps.length).toBe(7);

    const tallyBtn = wf2Section.query(By.css('#hiwTallyCta'));
    expect(tallyBtn).toBeTruthy();
    expect(tallyBtn.attributes['routerLink']).toBe('/excel-to-tally');
  });

  it('6. should display honest engineering research roadmap note for Bills & Invoices', () => {
    component.setWorkflow('bills-invoices');
    fixture.detectChanges();

    const wf3Section = fixture.debugElement.query(By.css('#sectionBillsInvoices'));
    expect(wf3Section).toBeTruthy();
    expect(wf3Section.nativeElement.textContent).toContain('Bills & Invoices → Excel');
    expect(wf3Section.nativeElement.textContent).toContain('In Research');
    expect(wf3Section.nativeElement.textContent).toContain('Accuracy > Feature Count');
  });

  it('7. should render digital PDF requirement and OCR advisory callout in Workflow 1', () => {
    component.setWorkflow('bank-to-excel');
    fixture.detectChanges();

    const advisory = fixture.debugElement.query(By.css('.hiw-advisory-box'));
    expect(advisory).toBeTruthy();
    expect(advisory.nativeElement.textContent).toContain('Digital PDF Requirement');
    expect(advisory.nativeElement.textContent).toContain('Scanned PDFs, camera photos, passbook printouts');
    expect(advisory.nativeElement.textContent).toContain('not currently included in the standard digital-PDF parser workflow');
  });

  it('8. should render bottom action CTAs for both active workflows', () => {
    const bottomConverter = fixture.debugElement.query(By.css('#hiwBottomConverterBtn'));
    const bottomTally = fixture.debugElement.query(By.css('#hiwBottomTallyBtn'));
    const bottomBanks = fixture.debugElement.query(By.css('#hiwBottomBanksBtn'));

    expect(bottomConverter).toBeTruthy();
    expect(bottomConverter.attributes['routerLink']).toBe('/converter');

    expect(bottomTally).toBeTruthy();
    expect(bottomTally.attributes['routerLink']).toBe('/excel-to-tally');

    expect(bottomBanks).toBeTruthy();
    expect(bottomBanks.attributes['routerLink']).toBe('/supported-banks');
  });
});
