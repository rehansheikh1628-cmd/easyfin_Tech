import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommonModule } from '@angular/common';
import { NO_ERRORS_SCHEMA, provideZonelessChangeDetection } from '@angular/core';
import { RouterModule } from '@angular/router';
import { By } from '@angular/platform-browser';
import { Observable, of } from 'rxjs';
import { describe, it, expect, beforeEach } from 'vitest';
import { FeaturesComponent } from './features.component';
import { AuthService } from '../../services/auth.service';

describe('FeaturesComponent (Phase 14 UI Redesign)', () => {
  let component: FeaturesComponent;
  let fixture: ComponentFixture<FeaturesComponent>;
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
      declarations: [FeaturesComponent],
      imports: [CommonModule, RouterModule.forRoot([])],
      providers: [
        provideZonelessChangeDetection(),
        { provide: AuthService, useValue: mockAuthService }
      ],
      schemas: [NO_ERRORS_SCHEMA]
    }).compileComponents();

    fixture = TestBed.createComponent(FeaturesComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('1. should create FeaturesComponent', () => {
    expect(component).toBeTruthy();
    expect(component.isAuthenticated).toBe(true);
  });

  it('2. should render main headline with factual value proposition', () => {
    const titleEl = fixture.debugElement.query(By.css('#featuresMainTitle'));
    expect(titleEl).toBeTruthy();
    expect(titleEl.nativeElement.textContent).toContain('Built for the work behind the numbers.');
  });

  it('3. should render Workflow 1 (Bank Statement to Excel) with direct CTA to /converter', () => {
    const wf1Section = fixture.debugElement.query(By.css('#workflowConverter'));
    expect(wf1Section).toBeTruthy();
    expect(wf1Section.nativeElement.textContent).toContain('Bank Statement PDF → Excel');

    const converterBtn = fixture.debugElement.query(By.css('#featuresConverterBtn'));
    expect(converterBtn).toBeTruthy();
    expect(converterBtn.attributes['routerLink']).toBe('/converter');
  });

  it('4. should render Workflow 2 (Excel to Tally XML) with direct CTA to /excel-to-tally', () => {
    const wf2Section = fixture.debugElement.query(By.css('#workflowTally'));
    expect(wf2Section).toBeTruthy();
    expect(wf2Section.nativeElement.textContent).toContain('Excel → Tally XML');

    const tallyBtn = fixture.debugElement.query(By.css('#featuresExcelToTallyBtn'));
    expect(tallyBtn).toBeTruthy();
    expect(tallyBtn.attributes['routerLink']).toBe('/excel-to-tally');
  });

  it('5. should render digital PDF requirement and honest OCR guidance in Workflow 1', () => {
    const notice = fixture.debugElement.query(By.css('.digital-notice-callout'));
    expect(notice).toBeTruthy();
    expect(notice.nativeElement.textContent).toContain('Digital PDF Requirement');
    expect(notice.nativeElement.textContent).toContain('Scanned statements and passbook photos require OCR and are not currently included');
  });

  it('6. should render link to /supported-banks', () => {
    const banksBtn = fixture.debugElement.query(By.css('#featuresViewBanksBtn'));
    expect(banksBtn).toBeTruthy();
    expect(banksBtn.attributes['routerLink']).toBe('/supported-banks');
  });

  it('7. should render Bills & Invoices as Coming Soon without claiming active OCR or GST', () => {
    const comingSoonCard = fixture.debugElement.query(By.css('.coming-soon-card'));
    expect(comingSoonCard).toBeTruthy();
    expect(comingSoonCard.nativeElement.textContent).toContain('Bills & Invoices → Excel');
    expect(comingSoonCard.nativeElement.textContent).toContain('Coming Soon');
    expect(comingSoonCard.nativeElement.textContent).toContain('Currently in engineering research');
  });

  it('8. should render bottom action CTAs for both active workflows', () => {
    const bottomConverter = fixture.debugElement.query(By.css('#featuresBottomConverterBtn'));
    const bottomTally = fixture.debugElement.query(By.css('#featuresBottomTallyBtn'));

    expect(bottomConverter).toBeTruthy();
    expect(bottomConverter.attributes['routerLink']).toBe('/converter');

    expect(bottomTally).toBeTruthy();
    expect(bottomTally.attributes['routerLink']).toBe('/excel-to-tally');
  });
});
